﻿using System;
using System.ServiceModel;
using PowerShellTools.Common;
using PowerShellTools.Common.ServiceManagement.IntelliSenseContract;
using System.ServiceModel;
using PowerShellTools.Common.ServiceManagement.DebuggingContract;
using PowerShellTools.DebugEngine;
using System.Diagnostics;
using log4net;

namespace PowerShellTools.ServiceManagement
{
    /// <summary>
    /// Manage the process and channel creation.
    /// </summary>
    internal sealed class ConnectionManager
    {
        private IPowershellIntelliSenseService _powershellIntelliSenseService;
        private IPowershellDebuggingService _powershellDebuggingService;
        private object _syncObject = new object();
        private static object _staticSyncObject = new object();
        private static ConnectionManager _instance;
        private Process _process;
        private ChannelFactory<IPowershellIntelliSenseService> _intelliSenseServiceChannelFactory;
        private ChannelFactory<IPowershellDebuggingService> _debuggingServiceChannelFactory;
        private static readonly ILog Log = LogManager.GetLogger(typeof(PowerShellToolsPackage));

        private ConnectionManager()
        {
            OpenClientConnection();
        }

        /// <summary>
        /// Connection manager instance.
        /// </summary>
        public static ConnectionManager Instance
        {
            get
            {
                lock (_staticSyncObject)
                {
                    if (_instance == null)
                    {
                        _instance = new ConnectionManager();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// The IntelliSense service channel.
        /// </summary>
        public IPowershellIntelliSenseService PowershellIntelliSenseSerivce
        {
            get
            {
                if (_powershellIntelliSenseService == null)
                {
                    OpenClientConnection();
                }

                return _powershellIntelliSenseService;
            }
        }

        /// <summary>
        /// The debugging service channel.
        /// </summary>
        public IPowershellDebuggingService PowershellDebuggingService
        {
            get
            {
                if (_powershellDebuggingService == null)
                {
                    OpenClientConnection();
                }
                return _powershellDebuggingService;
            }
        }

        private void OpenClientConnection()
        {
            lock (_syncObject)
            {
                if (_powershellIntelliSenseService == null || _powershellDebuggingService == null)
                {
                    EnsureCloseProcess(_process);
                    var hostProcess = PowershellHostProcessHelper.CreatePowershellHostProcess();
                    _process = hostProcess.Process;
                    _process.Exited += ConnectionExceptionHandler;

                    // net.pipe://localhost/UniqueEndpointGuid/{RelativeUri}
                    var intelliSenseServiceEndPointAddress = Constants.ProcessManagerHostUri + hostProcess.EndpointGuid + "/" + Constants.IntelliSenseHostRelativeUri;
                    var deubggingServiceEndPointAddress = Constants.ProcessManagerHostUri + hostProcess.EndpointGuid + "/" + Constants.DebuggingHostRelativeUri;

                    try
                    {
                        _intelliSenseServiceChannelFactory = ChannelFactoryHelper.CreateChannelFactory<IPowershellIntelliSenseService>(intelliSenseServiceEndPointAddress);
                        _intelliSenseServiceChannelFactory.Faulted += ConnectionExceptionHandler;
                        _intelliSenseServiceChannelFactory.Closed += ConnectionExceptionHandler;
                        _intelliSenseServiceChannelFactory.Open();
                        _powershellIntelliSenseService = _intelliSenseServiceChannelFactory.CreateChannel();

                        _debuggingServiceChannelFactory = ChannelFactoryHelper.CreateDuplexChannelFactory<IPowershellDebuggingService>(deubggingServiceEndPointAddress, new InstanceContext(new DebugServiceEventsHandlerProxy()));
                        _debuggingServiceChannelFactory.Faulted += ConnectionExceptionHandler;
                        _debuggingServiceChannelFactory.Closed += ConnectionExceptionHandler;
                        _debuggingServiceChannelFactory.Open();
                        _powershellDebuggingService = _debuggingServiceChannelFactory.CreateChannel();                        
                    }
                    catch
                    {
                        // Connection has to be established...
                        Log.Error("Connection establish failed...");

                        _powershellIntelliSenseService = null;
                        _powershellDebuggingService = null;
                        throw;
                    }
                }
            }
        }

        private void ConnectionExceptionHandler(object sender, EventArgs e)
        {
            EnsureClearServiceChannel();
        }

        private void EnsureCloseProcess(Process process)
        {
            if (process != null)
            {
                try
                {
                    process.Kill();
                    process = null;
                }
                catch
                {
                    //TODO: log excetion info here
                }
            }
        }

        private void EnsureClearServiceChannel()
        {
            if (_intelliSenseServiceChannelFactory != null)
            {
                _intelliSenseServiceChannelFactory.Abort();
                _intelliSenseServiceChannelFactory = null;
                _powershellIntelliSenseService = null;
            }

            if (_debuggingServiceChannelFactory != null)
            {
                _debuggingServiceChannelFactory.Abort();
                _debuggingServiceChannelFactory = null;
                _powershellDebuggingService = null;
            }

        }
    }
}
