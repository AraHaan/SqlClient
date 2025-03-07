// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Data.Common;

namespace Microsoft.Data.SqlClient
{
    // This is a singleton instance per AppDomain that acts as the notification dispatcher for
    // that AppDomain.  It receives calls from the SqlDependencyProcessDispatcher with an ID or a server name
    // to invalidate matching dependencies in the given AppDomain.

    internal partial class SqlDependencyPerAppDomainDispatcher : MarshalByRefObject
    {
        // Instance members

        internal static readonly SqlDependencyPerAppDomainDispatcher SingletonInstance = new(); // singleton object

        internal object _instanceLock = new();

        // Dependency ID -> Dependency hashtable.  1 -> 1 mapping.
        // 1) Used for ASP.NET to map from ID to dependency.
        // 2) Used to enumerate dependencies to invalidate based on server.
        private readonly Dictionary<string, SqlDependency> _dependencyIdToDependencyHash;

        // holds dependencies list per notification and the command hash from which this notification was generated
        // command hash is needed to remove its entry from _commandHashToNotificationId when the notification is removed
        private sealed class DependencyList : List<SqlDependency>
        {
            public readonly string CommandHash;

            internal DependencyList(string commandHash)
            {
                CommandHash = commandHash;
            }
        }

        // notificationId -> Dependencies hashtable:  1 -> N mapping.  notificationId == appDomainKey + commandHash.
        // More than one dependency can be using the same command hash values resulting in a hash to the same value.
        // We use this to cache mapping between command to dependencies such that we may reduce the notification
        // resource effect on SQL Server.  The Guid identifier is sent to the server during notification enlistment,
        // and returned during the notification event.  Dependencies look up existing Guids, if one exists, to ensure
        // they are re-using notification ids.
        private readonly Dictionary<string, DependencyList> _notificationIdToDependenciesHash;

        // CommandHash value -> notificationId associated with it:  1->1 mapping. This map is used to quickly find if we need to create
        // new notification or hookup into existing one.
        // CommandHash is built from connection string, command text and parameters
        private readonly Dictionary<string, string> _commandHashToNotificationId;

        // TIMEOUT LOGIC DESCRIPTION
        //
        // Every time we add a dependency we compute the next, earlier timeout.
        //
        // We setup a timer to get a callback every 15 seconds. In the call back:
        // - If there are no active dependencies, we just return.
        // - If there are dependencies but none of them timed-out (compared to the "next timeout"),
        //   we just return.
        // - Otherwise we Invalidate() those that timed-out.
        //
        // So the client-generated timeouts have a granularity of 15 seconds. This allows
        // for a simple and low-resource-consumption implementation.
        //
        // LOCKS: don't update _nextTimeout outside of the _dependencyHash.SyncRoot lock.

        private bool _sqlDependencyTimeOutTimerStarted = false;
        // Next timeout for any of the dependencies in the dependency table.
        private DateTime _nextTimeout;
        // Timer to periodically check the dependencies in the table and see if anyone needs
        // a timeout.  We'll enable this only on demand.
        private readonly Timer _timeoutTimer;

        private static int s_objectTypeCount; // EventSource counter
        internal int ObjectID { get; } = Interlocked.Increment(ref s_objectTypeCount);

        private SqlDependencyPerAppDomainDispatcher()
        {
            long scopeID = SqlClientEventSource.Log.TryNotificationScopeEnterEvent("<sc.SqlDependencyPerAppDomainDispatcher|DEP> {0}", ObjectID);
            try
            {
                _dependencyIdToDependencyHash = new Dictionary<string, SqlDependency>();
                _notificationIdToDependenciesHash = new Dictionary<string, DependencyList>();
                _commandHashToNotificationId = new Dictionary<string, string>();
#if NETFRAMEWORK
                _timeoutTimer = new Timer(new TimerCallback(TimeoutTimerCallback), null, Timeout.Infinite, Timeout.Infinite);

                // If rude abort - we'll leak.  This is acceptable for now.  
                AppDomain.CurrentDomain.DomainUnload += new EventHandler(UnloadEventHandler);
#else
                _timeoutTimer = ADP.UnsafeCreateTimer(
                    new TimerCallback(TimeoutTimerCallback),
                    null,
                    Timeout.Infinite,
                    Timeout.Infinite);

                SubscribeToAppDomainUnload();
                SubscribeToAssemblyLoadContextUnload();
#endif // NETFRAMEWORK
            }
            finally
            {
                SqlClientEventSource.Log.TryNotificationScopeLeaveEvent(scopeID);
            }
        }

#if NET
        partial void SubscribeToAppDomainUnload();

        partial void SubscribeToAssemblyLoadContextUnload();
#endif
        private void UnloadEventHandler(object sender, EventArgs e)
        {
            long scopeID = SqlClientEventSource.Log.TryNotificationScopeEnterEvent("SqlDependencyPerAppDomainDispatcher.UnloadEventHandler | DEP | Object Id {0}", ObjectID);
            try
            {
                // Make non-blocking call to ProcessDispatcher to ThreadPool.QueueUserWorkItem to complete
                // stopping of all start calls in this AppDomain.  For containers shared among various AppDomains,
                // this will just be a ref-count subtract.  For non-shared containers, we will close the container
                // and clean-up.
                SqlDependencyProcessDispatcher dispatcher = SqlDependency.ProcessDispatcher;
                dispatcher?.QueueAppDomainUnloading(SqlDependency.AppDomainKey);
            }
            finally
            {
                SqlClientEventSource.Log.TryNotificationScopeLeaveEvent(scopeID);
            }
        }

        //  When remoted across appdomains, MarshalByRefObject links by default time out if there is no activity 
        //  within a few minutes.  Add this override to prevent marshaled links from timing out.
#if NET
        [Obsolete("InitializeLifetimeService() is not supported after .Net5.0 and throws PlatformNotSupportedException.")]
#endif
        public override object InitializeLifetimeService()
        {
            return null;
        }

        // Methods for dependency hash manipulation and firing.

        // This method is called upon SqlDependency constructor.
        internal void AddDependencyEntry(SqlDependency dep)
        {
            long scopeID = SqlClientEventSource.Log.TryNotificationScopeEnterEvent("<sc.SqlDependencyPerAppDomainDispatcher.AddDependencyEntry|DEP> {0}, SqlDependency: {1}", ObjectID, dep.ObjectID);
            try
            {
                lock (_instanceLock)
                {
                    _dependencyIdToDependencyHash.Add(dep.Id, dep);
                }
            }
            finally
            {
                SqlClientEventSource.Log.TryNotificationScopeLeaveEvent(scopeID);
            }
        }

        // This method is called upon Execute of a command associated with a SqlDependency object.
        internal string AddCommandEntry(string commandHash, SqlDependency dep)
        {
            long scopeID = SqlClientEventSource.Log.TryNotificationScopeEnterEvent("<sc.SqlDependencyPerAppDomainDispatcher.AddCommandEntry|DEP> {0}, commandHash: '{1}', SqlDependency: {2}", ObjectID, commandHash, dep.ObjectID);
            string notificationId = string.Empty;
            try
            {
                lock (_instanceLock)
                {
                    if (!_dependencyIdToDependencyHash.ContainsKey(dep.Id))
                    {
                        // Determine if depId->dep hashtable contains dependency.  If not, it's been invalidated.
                        SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.AddCommandEntry|DEP> Dependency not present in depId->dep hash, must have been invalidated.");
                    }
                    else
                    {
                        // check if we already have notification associated with given command hash
                        if (_commandHashToNotificationId.TryGetValue(commandHash, out notificationId))
                        {
                            // we have one or more SqlDependency instances with same command hash

                            if (!_notificationIdToDependenciesHash.TryGetValue(notificationId, out DependencyList dependencyList))
                            {
                                // this should not happen since _commandHashToNotificationId and _notificationIdToDependenciesHash are always
                                // updated together
                                Debug.Fail("_commandHashToNotificationId has entries that were removed from _notificationIdToDependenciesHash. Remember to keep them in sync");
                                throw ADP.InternalError(ADP.InternalErrorCode.SqlDependencyCommandHashIsNotAssociatedWithNotification);
                            }

                            // join the new dependency to the list
                            if (!dependencyList.Contains(dep))
                            {
                                SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.AddCommandEntry|DEP> Dependency not present for commandHash, adding.");
                                dependencyList.Add(dep);
                            }
                            else
                            {
                                SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.AddCommandEntry|DEP> Dependency already present for commandHash.");
                            }
                        }
                        else
                        {
                            // we did not find notification ID with the same app domain and command hash, create a new one
                            // use unique guid to avoid duplicate IDs
                            // prepend app domain ID to the key - SqlConnectionContainer::ProcessNotificationResults (SqlDependencyListener.cs)
                            // uses this app domain ID to route the message back to the app domain in which this SqlDependency was created
                            notificationId = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "{0};{1}",
                                SqlDependency.AppDomainKey, // must be first
                                Guid.NewGuid().ToString("D", System.Globalization.CultureInfo.InvariantCulture)
                                );
                            SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.AddCommandEntry|DEP> Creating new Dependencies list for commandHash.");
                            DependencyList dependencyList = new(commandHash);
                            dependencyList.Add(dep);

                            // map command hash to notification we just created to reuse it for the next client
                            _commandHashToNotificationId.Add(commandHash, notificationId);
                            _notificationIdToDependenciesHash.Add(notificationId, dependencyList);
                        }

                        Debug.Assert(_notificationIdToDependenciesHash.Count == _commandHashToNotificationId.Count, "always keep these maps in sync!");
                    }
                }

                return notificationId;
            }
            finally
            {
                SqlClientEventSource.Log.TryNotificationScopeLeaveEvent(scopeID);
            }
        }

        // This method is called by the ProcessDispatcher upon a notification for this AppDomain.
        internal void InvalidateCommandID(SqlNotification sqlNotification)
        {
            long scopeID = SqlClientEventSource.Log.TryNotificationScopeEnterEvent("<sc.SqlDependencyPerAppDomainDispatcher.InvalidateCommandID|DEP> {0}, commandHash: '{1}'", ObjectID, sqlNotification.Key);
            try
            {
                List<SqlDependency> dependencyList = null;

                lock (_instanceLock)
                {
                    dependencyList = LookupCommandEntryWithRemove(sqlNotification.Key);

                    if (dependencyList != null)
                    {
                        SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.InvalidateCommandID|DEP> commandHash found in hashtable.");
                        foreach (SqlDependency dependency in dependencyList)
                        {
                            // Ensure we remove from process static app domain hash for dependency initiated invalidates.
                            LookupDependencyEntryWithRemove(dependency.Id);

                            // Completely remove Dependency from commandToDependenciesHash.
                            RemoveDependencyFromCommandToDependenciesHash(dependency);
                        }
                    }
                    else
                    {
                        SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.InvalidateCommandID|DEP> commandHash NOT found in hashtable.");
                    }
                }

                if (dependencyList != null)
                {
                    // After removal from hashtables, invalidate.
                    foreach (SqlDependency dependency in dependencyList)
                    {
                        SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.InvalidateCommandID|DEP> Dependency found in commandHash dependency ArrayList - calling invalidate.");
                        try
                        {
                            dependency.Invalidate(sqlNotification.Type, sqlNotification.Info, sqlNotification.Source);
                        }
                        catch (Exception e)
                        {
                            // Since we are looping over dependencies, do not allow one Invalidate
                            // that results in a throw prevent us from invalidating all dependencies
                            // related to this server.
                            if (!ADP.IsCatchableExceptionType(e))
                            {
                                throw;
                            }
                            ADP.TraceExceptionWithoutRethrow(e);
                        }
                    }
                }
            }
            finally
            {
                SqlClientEventSource.Log.TryNotificationScopeLeaveEvent(scopeID);
            }
        }

        // This method is called when a connection goes down or other unknown error occurs in the ProcessDispatcher.
        internal void InvalidateServer(string server, SqlNotification sqlNotification)
        {
            long scopeID = SqlClientEventSource.Log.TryNotificationScopeEnterEvent("<sc.SqlDependencyPerAppDomainDispatcher.Invalidate|DEP> {0}, server: '{1}'", ObjectID, server);
            try
            {
                List<SqlDependency> dependencies = new();

                lock (_instanceLock)
                { // Copy inside of lock, but invalidate outside of lock.
                    foreach (KeyValuePair<string, SqlDependency> entry in _dependencyIdToDependencyHash)
                    {
                        SqlDependency dependency = entry.Value;
                        if (dependency.ContainsServer(server))
                        {
                            dependencies.Add(dependency);
                        }
                    }

                    foreach (SqlDependency dependency in dependencies)
                    { // Iterate over resulting list removing from our hashes.
                      // Ensure we remove from process static app domain hash for dependency initiated invalidates.
                        LookupDependencyEntryWithRemove(dependency.Id);

                        // Completely remove Dependency from commandToDependenciesHash.
                        RemoveDependencyFromCommandToDependenciesHash(dependency);
                    }
                }

                foreach (SqlDependency dependency in dependencies)
                { // Iterate and invalidate.
                    try
                    {
                        dependency.Invalidate(sqlNotification.Type, sqlNotification.Info, sqlNotification.Source);
                    }
                    catch (Exception e)
                    {
                        // Since we are looping over dependencies, do not allow one Invalidate
                        // that results in a throw prevent us from invalidating all dependencies
                        // related to this server.
                        if (!ADP.IsCatchableExceptionType(e))
                        {
                            throw;
                        }
                        ADP.TraceExceptionWithoutRethrow(e);
                    }
                }
            }
            finally
            {
                SqlClientEventSource.Log.TryNotificationScopeLeaveEvent(scopeID);
            }
        }

        // This method is called by SqlCommand to enable ASP.NET scenarios - map from ID to Dependency.
        internal SqlDependency LookupDependencyEntry(string id)
        {
            long scopeID = SqlClientEventSource.Log.TryNotificationScopeEnterEvent("<sc.SqlDependencyPerAppDomainDispatcher.LookupDependencyEntry|DEP> {0}, Key: '{1}'", ObjectID, id);
            try
            {
                if (id == null)
                {
                    throw ADP.ArgumentNull(nameof(id));
                }
                if (string.IsNullOrEmpty(id))
                {
                    throw SQL.SqlDependencyIdMismatch();
                }

                SqlDependency entry = null;

                lock (_instanceLock)
                {
                    if (_dependencyIdToDependencyHash.ContainsKey(id))
                    {
                        entry = _dependencyIdToDependencyHash[id];
                    }
                    else
                    {
                        SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.LookupDependencyEntry|DEP|ERR> ERROR - dependency ID mismatch - not throwing.");
                    }
                }

                return entry;
            }
            finally
            {
                SqlClientEventSource.Log.TryNotificationScopeLeaveEvent(scopeID);
            }
        }

        // Remove the dependency from the hashtable with the passed id.
        private void LookupDependencyEntryWithRemove(string id)
        {
            long scopeID = SqlClientEventSource.Log.TryNotificationScopeEnterEvent("<sc.SqlDependencyPerAppDomainDispatcher.LookupDependencyEntryWithRemove|DEP> {0}, id: '{1}'", ObjectID, id);
            try
            {
                lock (_instanceLock)
                {
                    if (_dependencyIdToDependencyHash.ContainsKey(id))
                    {
                        SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.LookupDependencyEntryWithRemove|DEP> Entry found in hashtable - removing.");
                        _dependencyIdToDependencyHash.Remove(id);

                        // if there are no more dependencies then we can dispose the timer.
                        if (0 == _dependencyIdToDependencyHash.Count)
                        {
                            _timeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
                            _sqlDependencyTimeOutTimerStarted = false;
                        }
                    }
                    else
                    {
                        SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.LookupDependencyEntryWithRemove|DEP> Entry NOT found in hashtable.");
                    }
                }
            }
            finally
            {
                SqlClientEventSource.Log.TryNotificationScopeLeaveEvent(scopeID);
            }
        }

        // Find and return arraylist, and remove passed hash value.
        private List<SqlDependency> LookupCommandEntryWithRemove(string notificationId)
        {
            long scopeID = SqlClientEventSource.Log.TryNotificationScopeEnterEvent("<sc.SqlDependencyPerAppDomainDispatcher.LookupCommandEntryWithRemove|DEP> {0}, commandHash: '{1}'", ObjectID, notificationId);
            try
            {
                DependencyList entry = null;

                lock (_instanceLock)
                {
                    if (_notificationIdToDependenciesHash.TryGetValue(notificationId, out entry))
                    {
                        SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.LookupDependencyEntriesWithRemove|DEP> Entries found in hashtable - removing.");

                        // update the tables
                        _notificationIdToDependenciesHash.Remove(notificationId);
                        // Cleanup the map between the command hash and associated notification ID
                        _commandHashToNotificationId.Remove(entry.CommandHash);
                    }
                    else
                    {
                        SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.LookupDependencyEntriesWithRemove|DEP> Entries NOT found in hashtable.");
                    }

                    Debug.Assert(_notificationIdToDependenciesHash.Count == _commandHashToNotificationId.Count, "always keep these maps in sync!");
                }

                return entry; // DependencyList inherits from List<SqlDependency>
            }
            finally
            {
                SqlClientEventSource.Log.TryNotificationScopeLeaveEvent(scopeID);
            }
        }

        // Remove from commandToDependenciesHash all references to the passed dependency.
        private void RemoveDependencyFromCommandToDependenciesHash(SqlDependency dependency)
        {
            long scopeID = SqlClientEventSource.Log.TryNotificationScopeEnterEvent("<sc.SqlDependencyPerAppDomainDispatcher.RemoveDependencyFromCommandToDependenciesHash|DEP> {0}, SqlDependency: {1}", ObjectID, dependency.ObjectID);
            try
            {
                lock (_instanceLock)
                {
                    List<string> notificationIdsToRemove = new();
                    List<string> commandHashesToRemove = new();

                    foreach (KeyValuePair<string, DependencyList> entry in _notificationIdToDependenciesHash)
                    {
                        DependencyList dependencies = entry.Value;
                        if (dependencies.Remove(dependency))
                        {
                            SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.RemoveDependencyFromCommandToDependenciesHash|DEP> Removed SqlDependency: {0}, with ID: '{1}'.", dependency.ObjectID, dependency.Id);
                            if (dependencies.Count == 0)
                            {
                                // this dependency was the last associated with this notification ID, remove the entry
                                // note: cannot do it inside foreach over dictionary
                                notificationIdsToRemove.Add(entry.Key);
                                commandHashesToRemove.Add(entry.Value.CommandHash);
                            }
                        }

                        // same SqlDependency can be associated with more than one command, so we have to continue till the end...
                    }

                    Debug.Assert(commandHashesToRemove.Count == notificationIdsToRemove.Count, "maps should be kept in sync");
                    for (int i = 0; i < notificationIdsToRemove.Count; i++)
                    {
                        // cleanup the entry outside of foreach
                        _notificationIdToDependenciesHash.Remove(notificationIdsToRemove[i]);
                        // Cleanup the map between the command hash and associated notification ID
                        _commandHashToNotificationId.Remove(commandHashesToRemove[i]);
                    }

                    Debug.Assert(_notificationIdToDependenciesHash.Count == _commandHashToNotificationId.Count, "always keep these maps in sync!");
                }
            }
            finally
            {
                SqlClientEventSource.Log.TryNotificationScopeLeaveEvent(scopeID);
            }
        }

        // Methods for Timer maintenance and firing.

        internal void StartTimer(SqlDependency dep)
        {
            long scopeID = SqlClientEventSource.Log.TryNotificationScopeEnterEvent("<sc.SqlDependencyPerAppDomainDispatcher.StartTimer|DEP> {0}, SqlDependency: {1}", ObjectID, dep.ObjectID);
            try
            {
                // If this dependency expires sooner than the current next timeout, change
                // the timeout and enable timer callback as needed.  Note that we change _nextTimeout
                // only inside the hashtable syncroot.
                lock (_instanceLock)
                {
                    // Enable the timer if needed (disable when empty, enable on the first addition).
                    if (!_sqlDependencyTimeOutTimerStarted)
                    {
                        SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.StartTimer|DEP> Timer not yet started, starting.");
                        _timeoutTimer.Change(15000 /* 15 secs */, 15000 /* 15 secs */);

                        // Save this as the earlier timeout to come.
                        _nextTimeout = dep.ExpirationTime;
                        _sqlDependencyTimeOutTimerStarted = true;
                    }
                    else if (_nextTimeout > dep.ExpirationTime)
                    {
                        SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.StartTimer|DEP> Timer already started, resetting time.");

                        // Save this as the earlier timeout to come.
                        _nextTimeout = dep.ExpirationTime;
                    }
                }
            }
            finally
            {
                SqlClientEventSource.Log.TryNotificationScopeLeaveEvent(scopeID);
            }
        }

        private static void TimeoutTimerCallback(object state)
        {
            long scopeID = SqlClientEventSource.Log.TryNotificationScopeEnterEvent("<sc.SqlDependencyPerAppDomainDispatcher.TimeoutTimerCallback|DEP> AppDomainKey: '{0}'", SqlDependency.AppDomainKey);
            try
            {
                SqlDependency[] dependencies;

                // Only take the lock for checking whether there is work to do
                // if we do have work, we'll copy the hashtable and scan it after releasing
                // the lock.
                lock (SingletonInstance._instanceLock)
                {
                    if (0 == SingletonInstance._dependencyIdToDependencyHash.Count)
                    {
                        SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.TimeoutTimerCallback|DEP> No dependencies, exiting.");

                        // Nothing to check.
                        return;
                    }
                    if (SingletonInstance._nextTimeout > DateTime.UtcNow)
                    {
                        SqlClientEventSource.Log.TryNotificationTraceEvent("<sc.SqlDependencyPerAppDomainDispatcher.TimeoutTimerCallback|DEP> No timeouts expired, exiting.");

                        // No dependency timed-out yet.
                        return;
                    }

                    // If at least one dependency timed-out do a scan of the table.
                    // NOTE: we could keep a shadow table sorted by expiration time, but
                    // given the number of typical simultaneously alive dependencies it's
                    // probably not worth the optimization.
                    dependencies = new SqlDependency[SingletonInstance._dependencyIdToDependencyHash.Count];
                    SingletonInstance._dependencyIdToDependencyHash.Values.CopyTo(dependencies, 0);
                }

                // Scan the active dependencies if needed.
                DateTime now = DateTime.UtcNow;
                DateTime newNextTimeout = DateTime.MaxValue;

                for (int i = 0; i < dependencies.Length; i++)
                {
                    // If expired fire the change notification.
                    if (dependencies[i].ExpirationTime <= now)
                    {
                        try
                        {
                            // This invokes user-code which may throw exceptions.
                            // NOTE: this is intentionally outside of the lock, we don't want
                            // to invoke user-code while holding an internal lock.
                            dependencies[i].Invalidate(SqlNotificationType.Change, SqlNotificationInfo.Error, SqlNotificationSource.Timeout);
                        }
                        catch (Exception e)
                        {
                            if (!ADP.IsCatchableExceptionType(e))
                            {
                                throw;
                            }

                            // This is an exception in user code, and we're in a thread-pool thread
                            // without user's code up in the stack, no much we can do other than
                            // eating the exception.
                            ADP.TraceExceptionWithoutRethrow(e);
                        }
                    }
                    else
                    {
                        if (dependencies[i].ExpirationTime < newNextTimeout)
                        {
                            newNextTimeout = dependencies[i].ExpirationTime; // Track the next earlier timeout.
                        }
                        dependencies[i] = null; // Null means "don't remove it from the hashtable" in the loop below.
                    }
                }

                // Remove timed-out dependencies from the hashtable.
                lock (SingletonInstance._instanceLock)
                {
                    for (int i = 0; i < dependencies.Length; i++)
                    {
                        if (dependencies[i] != null)
                        {
                            SingletonInstance._dependencyIdToDependencyHash.Remove(dependencies[i].Id);
                        }
                    }
                    if (newNextTimeout < SingletonInstance._nextTimeout)
                    {
                        SingletonInstance._nextTimeout = newNextTimeout; // We're inside the lock so ok to update.
                    }
                }
            }
            finally
            {
                SqlClientEventSource.Log.TryNotificationScopeLeaveEvent(scopeID);
            }
        }
    }

    // Simple class used to encapsulate all data in a notification.
    internal class SqlNotification : MarshalByRefObject
    {
        // This class could be Serializable rather than MBR...

        private readonly SqlNotificationInfo _info;
        private readonly SqlNotificationSource _source;
        private readonly SqlNotificationType _type;
        private readonly string _key;

        internal SqlNotification(SqlNotificationInfo info, SqlNotificationSource source, SqlNotificationType type, string key)
        {
            _info = info;
            _source = source;
            _type = type;
            _key = key;
        }

        internal SqlNotificationInfo Info => _info;

        internal string Key => _key;

        internal SqlNotificationSource Source => _source;

        internal SqlNotificationType Type => _type;
    }
}

