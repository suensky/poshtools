﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using EnvDTE80;
using Microsoft.PowerShell;
using Microsoft.VisualBasic;
using Microsoft.VisualStudio.Repl;
using Thread = System.Threading.Thread;

namespace PowerShellTools.DebugEngine
{
#if POWERSHELL
    using IReplWindow = IPowerShellReplWindow;
    using PowerShellTools.Common.ServiceManagement.DebuggingContract;
using Microsoft.VisualStudio.Shell.Interop;
#endif



    /// <summary>
    ///     The PoshTools PowerShell host and debugger.
    /// </summary>
    public partial class ScriptDebugger 
    {
        private readonly Guid _instanceId = Guid.NewGuid();
        private readonly CultureInfo _originalCultureInfo = Thread.CurrentThread.CurrentCulture;
        private readonly CultureInfo _originalUiCultureInfo = Thread.CurrentThread.CurrentUICulture;
        private Runspace _runspace;
        private readonly RunspaceRef _runspaceRef;
        private IPowershellDebuggingService _debuggingService;

        public IPowershellDebuggingService DebuggingService {
            get
            {
                return _debuggingService;
            }
            set
            {
                _debuggingService = value;
            }
        }

        public Runspace Runspace
        {
            get
            {
                return _runspace;
            }
            set
            {
                _runspace = value;
            }
        }

        private ScriptDebugger()
        {
            //TODO: remove once user prompt work is finished for debugging
            _runspace = RunspaceFactory.CreateRunspace();
            _runspace.Open();
            HostUi = new HostUi();
        }

        public ScriptDebugger(bool overrideExecutionPolicy)
            : this(overrideExecutionPolicy, PowerShellToolsPackage.DebuggingService){}

        public ScriptDebugger(bool overrideExecutionPolicy, IPowershellDebuggingService service)
            : this()
        {
            OverrideExecutionPolicy = overrideExecutionPolicy;
            _debuggingService = service;
            _debuggingService.SetRunspace(overrideExecutionPolicy);
        }

        public HostUi HostUi { get; private set; }
        public bool OverrideExecutionPolicy { get; private set; }

        public IReplWindow ReplWindow
        {
            get { return HostUi.ReplWindow; }
            set
            {
                HostUi.ReplWindow = value;
                if (value != null)
                {
                    RefreshPrompt();
                }
            }
        }

        /// <summary>
        ///     Refreshes the prompt in the REPL window to match the current PowerShell prompt value.
        /// </summary>
        public void RefreshPrompt()
        {
            if (HostUi != null && HostUi.ReplWindow != null)
                HostUi.ReplWindow.SetOptionValue(ReplOptions.CurrentPrimaryPrompt, GetPrompt());
        }

        private string GetPrompt()
        {
            try
            {
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.Runspace = _runspace;
                    ps.AddCommand("prompt");
                    return ps.Invoke<string>().FirstOrDefault();
                }
            }
            catch
            {
                return String.Empty;
            }
        }
    }

    public class HostUi
    {
        public IReplWindow ReplWindow { get; set; }

        public Action<String> OutputString { get; set; }

        /// <summary>
        /// Read host from user input
        /// </summary>
        /// <returns>user input string</returns>
        public string ReadLine()
        {
            return Interaction.InputBox("Read-Host", "Read-Host");
        }

        /// <summary>
        /// Output string from debugger in VS output/REPL pane window
        /// </summary>
        /// <param name="output"></param>
        public void VsOutputString(string output)
        {
            if (ReplWindow != null)
            {
                if (output.StartsWith(PowerShellConstants.PowershellOutputErrorTag))
                {
                    ReplWindow.WriteError(output);
                }
                else
                {
                    ReplWindow.WriteOutput(output);
                }
            }

            if (OutputString != null)
            {
                OutputString(output);
            }
        }

        public void VSOutputProgress(string label, int percentage)
        {
            var statusBar = (IVsStatusbar)PowerShellToolsPackage.Instance.GetService(typeof(SVsStatusbar));
            uint cookie = 0;
            statusBar.Progress(ref cookie, 1, label, (uint)percentage, 100);

            if (percentage == 100)
            {
                statusBar.Progress(ref cookie, 1, "", 0, 0);
            }
        }
    }
}