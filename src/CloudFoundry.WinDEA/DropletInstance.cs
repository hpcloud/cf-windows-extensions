﻿namespace CloudFoundry.WinDEA
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Sockets;
    using System.Threading;
    using CloudFoundry.WinDEA.Messages;
    using CloudFoundry.Utilities;
    using CloudFoundry.Utilities.Json;

    /// <summary>
    /// Represents a droplet instance.
    /// </summary>
    public class DropletInstance : IDisposable
    {
        /// <summary>
        /// The total usage history samples to store.
        /// </summary>
        public const int MaxUsageSamples = 30;

        /// <summary>
        /// The lock for the droplet instance.
        /// </summary>
        private ReaderWriterLockSlim readerWriterLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

        /// <summary>
        /// Properties for the droplet instance which are saved when snapshooting the applications.
        /// </summary>
        private DropletInstanceProperties properties = new DropletInstanceProperties();

        /// <summary>
        /// The history of resource usage of the instance.
        /// </summary>
        private List<DropletInstanceUsage> usage = new List<DropletInstanceUsage>();

        /// <summary>
        /// The Windows Job Object for the application instance. Used for security/resource sandboxing.
        /// </summary>
        private CloudFoundry.WindowsPrison.Prison processPrison = new CloudFoundry.WindowsPrison.Prison();

        /// <summary>
        /// Initializes a new instance of the <see cref="DropletInstance"/> class.
        /// </summary>
        public DropletInstance()
        {
            this.TotalTerminatedProcessesTracked = 0;        
        }

        public CloudFoundry.WindowsPrison.Prison Prison
        {
            get
            {
                return this.processPrison;
            }
            set
            {
                this.processPrison = value;
            }
        }

        /// <summary>
        /// Gets or sets tracks the terminated processes for logging.
        /// </summary>
        public int TotalTerminatedProcessesTracked
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the instances lock.
        /// </summary>
        public ReaderWriterLockSlim Lock
        {
            get
            {
                return this.readerWriterLock;
            }

            set
            {
                this.readerWriterLock = value;
            }
        }

        /// <summary>
        /// Gets or sets the instance properties.
        /// </summary>
        public DropletInstanceProperties Properties
        {
            get
            {
                return this.properties;
            }

            set
            {
                this.properties = value;
            }
        }

        /// <summary>
        /// Gets or sets usage history for the droplet instance.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists", Justification = "It is used for JSON (de)serialization."), 
        System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly", Justification = "Suitable for this context.")]
        public List<DropletInstanceUsage> Usage
        {
            get
            {
                return this.usage;
            }

            set
            {
                this.usage = value;
            }
        }

        /// <summary>
        /// Returns the heartbeat info of the current droplet instance.
        /// </summary>
        /// <returns>The requested heartbeat info.</returns>
        public HeartbeatMessage.InstanceHeartbeat GenerateInstanceHeartbeat()
        {
            HeartbeatMessage.InstanceHeartbeat beat = new HeartbeatMessage.InstanceHeartbeat();
            try
            {
                this.Lock.EnterReadLock();

                beat.DropletId = this.Properties.DropletId;
                beat.Version = this.Properties.Version;
                beat.InstanceId = this.Properties.InstanceId;
                beat.DockerId = this.Properties.InstanceId;
                beat.InstanceIndex = this.Properties.InstanceIndex;
                beat.State = this.Properties.State;
                beat.StateTimestamp = this.Properties.StateTimestamp;
                beat.CloudControllerPartition = this.Properties.CloudControllerPartition;
            }
            finally
            {
                this.Lock.ExitReadLock();
            }

            return beat;
        }

        /// <summary>
        /// Indicating whether this instance has opened up a port and it's listening on it.
        /// </summary>
        /// <param name="timeout">The time out of the TCP connection.</param>
        /// <returns>
        ///   <c>true</c> if [is port ready] [the specified time out]; otherwise, <c>false</c>.
        /// </returns>
        public bool IsPortReady(int timeout)
        {
            using (AutoResetEvent connectedEvent = new AutoResetEvent(false))
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult result = client.BeginConnect("localhost", this.properties.Port, null, null);
                    result.AsyncWaitHandle.WaitOne(timeout);

                    if (client.Connected)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Indicating whether this instance has opened up a port and it's listening on it with 150 ms timeout.
        /// </summary>
        /// <returns>
        ///   <c>true</c> if [is port ready]; otherwise, <c>false</c>.
        /// </returns>
        public bool IsPortReady()
        {
            return this.IsPortReady(150);
        }

        /// <summary>
        /// returns the instances exited message
        /// </summary>
        /// <returns>A DropletExitedMessage instance that has all members initialized.</returns>
        public DropletExitedMessage GenerateDropletExitedMessage()
        {
            DropletExitedMessage response = new DropletExitedMessage();

            try
            {
                this.Lock.EnterReadLock();
                response.DropletId = this.Properties.DropletId;
                response.Version = this.Properties.Version;
                response.InstanceId = this.Properties.InstanceId;
                response.Index = this.Properties.InstanceIndex;
                response.ExitReason = this.Properties.ExitReason;

                if (this.Properties.State == DropletInstanceState.Crashed)
                {
                    response.CrashedTimestamp = this.Properties.StateTimestamp;
                }

                response.ExitStatus = -1;
                response.ExitDescription = string.Empty;
            }
            finally
            {
                this.Lock.ExitReadLock();
            }

            return response;
        }

        /// <summary>
        /// Generates a status message reflecting the properties of the current droplet instance.
        /// </summary>
        /// <returns>The generated status message.</returns>
        public DropletStatusMessageResponse GenerateDropletStatusMessage()
        {
            DropletStatusMessageResponse response = new DropletStatusMessageResponse();

            try
            {
                this.Lock.EnterReadLock();
                response.Name = this.Properties.Name;
                response.Port = this.Properties.Port;
                response.Uris = this.Properties.Uris;
                response.Uptime = (DateTime.Now - this.Properties.Start).TotalSeconds;
                response.MemoryQuotaBytes = this.Properties.MemoryQuotaBytes;
                response.DiskQuotaBytes = this.Properties.DiskQuotaBytes;
                response.FDSQuota = this.Properties.FDSQuota;
                if (this.Usage.Count > 0)
                {
                    response.Usage = this.Usage[this.Usage.Count - 1];
                }
            }
            finally
            {
                this.Lock.ExitReadLock();
            }

            return response;
        }

        /// <summary>
        /// Updates the usage and adds it to the usage history.
        /// </summary>
        /// <param name="memoryBytes">The memory bytes.</param>
        /// <param name="cpu">The cpu.</param>
        /// <param name="diskBytes">The disk memory in bytes.</param>
        /// /// <param name="totalTicks">Total ticks of the process at this time.</param>
        /// <returns>A droplet instance usage instance containing all the usage information.</returns>
        public DropletInstanceUsage AddUsage(long memoryBytes, float cpu, long diskBytes, long totalTicks)
        {
            DropletInstanceUsage curUsage = new DropletInstanceUsage();
            curUsage.Time = DateTime.Now;
            curUsage.Cpu = cpu;
            curUsage.MemoryBytes = memoryBytes;
            curUsage.DiskBytes = diskBytes;
            curUsage.TotalProcessTicks = totalTicks;

            this.Usage.Add(curUsage);
            if (this.Usage.Count > DropletInstance.MaxUsageSamples)
            {
                this.Usage.RemoveAt(0);
            }

            this.Properties.LastUsage = curUsage;
            return curUsage;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (this.readerWriterLock != null)
                {
                    this.readerWriterLock.Dispose();
                }
            }
        }

        /// <summary>
        /// This class contains information about which type of plugin to load, and how to configure it.
        /// Information comes from the cloud controller stager, more specifically a framework plugin.rb file.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "It is used for JSON (de)serialization.")]
        internal class VcapPluginStagingInfoLogs : JsonConvertibleObject
        {
            /// <summary>
            /// Gets or sets the DEA error log.
            /// </summary>
            [JsonName("dea_error")]
            public string DeaErrorLog
            {
                get;
                set;
            }
        }

        /// <summary>
        /// The staging information sent by the cloud controller with the droplet bits.
        /// </summary>
        internal class VcapPluginStagingInfo : JsonConvertibleObject
        {
            /// <summary>
            /// Gets or sets the assembly of the plug-in.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "It is used for JSON (de)serialization."),
            JsonName("assembly")]
            public string Assembly { get; set; }

            /// <summary>
            /// Gets or sets the name of the class name of the plug-in.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "It is used for JSON (de)serialization."),
            JsonName("class_name")]
            public string ClassName { get; set; }

            /// <summary>
            /// Gets or sets the logs.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "It is used for JSON (de)serialization."),
            JsonName("logs")]
            public VcapPluginStagingInfoLogs Logs
            {
                get;
                set;
            }
        }
    }
}
