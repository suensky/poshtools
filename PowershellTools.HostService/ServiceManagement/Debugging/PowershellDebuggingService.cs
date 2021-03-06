﻿using Microsoft.PowerShell;
using PowerShellTools.Common.ServiceManagement.DebuggingContract;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PowerShellTools.HostService.ServiceManagement.Debugging
{
    [ServiceBehavior(ConcurrencyMode = ConcurrencyMode.Multiple)]
    public partial class PowershellDebuggingService : IPowershellDebuggingService
    {
        private static Runspace _runspace;
        private PowerShell _currentPowerShell;
        private IDebugEngineCallback _callback;
        private DebuggerResumeAction _resumeAction;
        private IEnumerable<PSObject> _varaiables;
        private IEnumerable<PSObject> _callstack;
        private string log;
        private Collection<PSVariable> _localVariables;
        private Dictionary<string, Object> _propVariables;
        private readonly AutoResetEvent _pausedEvent = new AutoResetEvent(false);

        public PowershellDebuggingService()
        {
            ServiceCommon.Log("Initializing debugging engine service ...");
            HostUi = new HostUi(this);
            _localVariables = new Collection<PSVariable>();
            _propVariables = new Dictionary<string, object>();
            InitializeRunspace(this);
        }

        /// <summary>
        ///     The runspace used by the current PowerShell host.
        /// </summary>
        public static Runspace Runspace 
        {
            get
            {
                return _runspace;
            }
        }

        public IDebugEngineCallback CallbackService
        {
            get
            {
                return _callback;
            }
            set 
            {
                _callback = value;
            }
        }

        #region Event handlers for debugger events inside service

        /// <summary>
        /// Runspace state change event handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _runspace_StateChanged(object sender, RunspaceStateEventArgs e)
        {
            Console.WriteLine("Runspace State Changed: {0}", e.RunspaceStateInfo.State);

            switch (e.RunspaceStateInfo.State)
            {
                case RunspaceState.Broken:
                case RunspaceState.Closed:
                case RunspaceState.Disconnected:
                    if (_callback != null)
                    {
                        _callback.DebuggerFinished();
                    }
                    break;
            }
        }

        /// <summary>
        /// Breakpoint updates (such as enabled/disabled)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Debugger_BreakpointUpdated(object sender, BreakpointUpdatedEventArgs e)
        {
            Console.WriteLine("Breakpoint updated: {0} {1}", e.UpdateType, e.Breakpoint);

            if (_callback != null)
            {
                var lbp = e.Breakpoint as LineBreakpoint;
                _callback.BreakpointUpdated(new DebuggerBreakpointUpdatedEventArgs(new PowershellBreakpoint(e.Breakpoint.Script, lbp.Line, lbp.Column), e.UpdateType));
            }
        }

        /// <summary>
        /// Debugging output event handler
        /// </summary>
        /// <param name="value">String to output</param>
        public void NotifyOutputString(string value)
        {
            ServiceCommon.Log("Callback to client for string output in VS", ConsoleColor.Yellow);
            if (_callback != null)
            {
                _callback.OutputString(value);
            }
        }

        /// <summary>
        /// Debugging output event handler
        /// </summary>
        /// <param name="value">String to output</param>
        public void NotifyOutputProgress(string label, int percentage)
        {
            ServiceCommon.Log("Callback to client to show progress", ConsoleColor.Yellow);
            if (_callback != null)
            {
                _callback.OutputProgress(label, percentage);
            }
        }

        /// <summary>
        /// PS debugger stopped event handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Debugger_DebuggerStop(object sender, DebuggerStopEventArgs e)
        {
            ServiceCommon.Log("Debugger stopped ...");
            RefreshScopedVariable();
            RefreshCallStack();


            ServiceCommon.Log("Callback to client, and wait for debuggee to resume", ConsoleColor.Yellow);
            if (e.Breakpoints.Count > 0)
            {
                LineBreakpoint bp = (LineBreakpoint)e.Breakpoints[0];
                if (_callback != null)
                {
                    _callback.DebuggerStopped(new DebuggerStoppedEventArgs(bp.Script, bp.Line, bp.Column));
                }
            }
            else
            {
                if (_callback != null)
                {
                    _callback.DebuggerStopped(new DebuggerStoppedEventArgs());
                }
            }
            _pausedEvent.WaitOne();
            ServiceCommon.Log(string.Format("Debuggee resume action is {0}", _resumeAction));
            e.ResumeAction = _resumeAction;
        }

        #endregion

        #region Debugging service calls

        /// <summary>
        /// Initialize of powershell runspace
        /// </summary>
        public void SetRunspace(bool overrideExecutionPolicy)
        {
            if (overrideExecutionPolicy)
            {
                SetupExecutionPolicy();
            }

            SetRunspace(_runspace);
        }

        /// <summary>
        /// Client respond with resume action to service
        /// </summary>
        /// <param name="action">Resumeaction from client</param>
        public void SetResumeAction(DebuggerResumeAction action)
        {
            ServiceCommon.Log("Client respond with resume action", ConsoleColor.Green);
            _resumeAction = action;
            _pausedEvent.Set();
        }

        /// <summary>
        /// Sets breakpoint for the current runspace.
        /// </summary>
        /// <param name="bp">Breakpoint to set</param>
        public void SetBreakpoint(PowershellBreakpoint bp)
        {
            ServiceCommon.Log("Setting breakpoing ...");
            using (var pipeline = (_runspace.CreatePipeline()))
            {
                var command = new Command("Set-PSBreakpoint");
                command.Parameters.Add("Script", bp.ScriptFullPath);
                command.Parameters.Add("Line", bp.Line);

                pipeline.Commands.Add(command);

                pipeline.Invoke();
            }
        }

        /// <summary>
        /// Clears existing breakpoints for the current runspace.
        /// </summary>
        public void ClearBreakpoints()
        {
            ServiceCommon.Log("ClearBreakpoints");

            IEnumerable<PSObject> breakpoints;
            using (var pipeline = (_runspace.CreatePipeline()))
            {
                var command = new Command("Get-PSBreakpoint");
                pipeline.Commands.Add(command);
                breakpoints = pipeline.Invoke();
            }

            if (!breakpoints.Any()) return;

            try
            {
                using (var pipeline = (_runspace.CreatePipeline()))
                {
                    var command = new Command("Remove-PSBreakpoint");
                    command.Parameters.Add("Breakpoint", breakpoints);
                    pipeline.Commands.Add(command);

                    pipeline.Invoke();
                }
            }
            catch (Exception)
            {
                ServiceCommon.Log("Failed to clear breakpoints.");
            }
        }

        /// <summary>
        /// Execute the specified command line from client
        /// </summary>
        /// <param name="commandLine">Command line to execute</param>
        public void Execute(string commandLine)
        {
            ServiceCommon.Log("Start executing ps script ...");

            if (_runspace.RunspaceAvailability != RunspaceAvailability.Available)
            {
                return;
            }
            try
            {
                if (_callback == null)
                {
                    _callback = OperationContext.Current.GetCallbackChannel<IDebugEngineCallback>();
                }
            }
            catch (Exception ex)
            {
                ServiceCommon.Log("No instance context retrieved.");
            }

            try
            {
                using (_currentPowerShell = PowerShell.Create())
                {
                    _currentPowerShell.Runspace = _runspace;
                    _currentPowerShell.AddScript(commandLine);

                    _currentPowerShell.AddCommand("out-default");
                    _currentPowerShell.Commands.Commands[0].MergeMyResults(PipelineResultTypes.Error, PipelineResultTypes.Output);

                    var objects = new PSDataCollection<PSObject>();
                    objects.DataAdded += objects_DataAdded;

                    _currentPowerShell.Invoke(null, objects);
                }
            }
            catch (Exception ex)
            {
                ServiceCommon.Log("Terminating error" + ex);
                if (_callback != null)
                {
                    _callback.OutputString("Error: " + ex.Message + Environment.NewLine);
                }

                OnTerminatingException(ex);
            }
            finally
            {
                _currentPowerShell.Stop();
                DebuggerFinished();
            }
        }


        /// <summary>
        /// Get all local scoped variables for client
        /// </summary>
        /// <returns>Collection of variable to client</returns>
        public Collection<Variable> GetScopedVariable()
        {
            Collection<Variable>  variables = new Collection<Variable>();

            foreach (var psobj in _varaiables)
            {
                var psVar = psobj.BaseObject as PSVariable;

                if (psVar != null)
                {
                    _localVariables.Add(psVar);
                    variables.Add(new Variable(psVar));
                }
            }

            return variables;
        }


        /// <summary>
        /// Expand IEnumerable to retrieve all elements
        /// </summary>
        /// <param name="varName">IEnumerable object name</param>
        /// <returns>Collection of variable to client</returns>
        public Collection<Variable> GetExpandedIEnumerableVariable(string varName)
        {
            ServiceCommon.Log("Client tries to watch an IEnumerable variable, dump its content ...");

            Collection<Variable> expandedVariable = new Collection<Variable>();

            var psVar = _localVariables.FirstOrDefault(v => v.Name == varName);
            object psVariable = (psVar == null) ? null : psVar.Value;
            
            if(psVariable == null)
            {
                psVariable = _propVariables[varName];
            }

            if (psVariable != null && psVariable is IEnumerable)
            {
                int i = 0;
                foreach (var item in (IEnumerable)psVariable)
                {
                    expandedVariable.Add(new Variable(String.Format("[{0}]", i), item.ToString(), item.GetType().ToString(), item is IEnumerable, item is PSObject));

                    if (!(item is string) && (item is IEnumerable || item is PSObject))
                    {
                        string key = string.Format("{0}\\{1}", varName, String.Format("[{0}]", i));
                        if(!_propVariables.ContainsKey(key))
                            _propVariables.Add(key, item);
                    }

                    i++;
                }
            }

            return expandedVariable;
        }


        /// <summary>
        /// Expand PSObject to retrieve all its properties
        /// </summary>
        /// <param name="varName">PSObject name</param>
        /// <returns>Collection of variable to client</returns>
        public Collection<Variable> GetPSObjectVariable(string varName)
        {
            ServiceCommon.Log("Client tries to watch an PSObject variable, dump its content ...");

            Collection<Variable> propsVariable = new Collection<Variable>();

            var psVar = _localVariables.FirstOrDefault(v => v.Name == varName);
            object psVariable = (psVar == null) ? null : psVar.Value;

            if (psVariable == null)
            {
                psVariable = _propVariables[varName];
            }

            if (psVariable != null && psVariable is PSObject)
            {
                foreach (var prop in ((PSObject)psVariable).Properties)
                {
                    if (propsVariable.Any(m => m.VarName == prop.Name))
                    {
                        continue;
                    }

                    object val;
                    try
                    {
                        val = prop.Value;
                    }
                    catch
                    {
                        val = "Failed to evaluate value.";
                    }

                    propsVariable.Add(new Variable(prop.Name, val.ToString(), val.GetType().ToString(), val is IEnumerable, val is PSObject));

                    if (!(val is string) && (val is IEnumerable || val is PSObject))
                    {
                        string key = string.Format("{0}\\{1}", varName, prop.Name);
                        if (!_propVariables.ContainsKey(key))
                            _propVariables.Add(key, val);
                    }
                }
            }

            return propsVariable;
        }

        /// <summary>
        /// Respond client request for callstack frames of current execution context
        /// </summary>
        /// <returns>Collection of callstack to client</returns>
        public IEnumerable<CallStack> GetCallStack()
        {
            ServiceCommon.Log("Obtaining the context for wcf callback");
            List<CallStackFrame> callStackFrames = new List<CallStackFrame>();

            foreach (var psobj in _callstack)
            {
                var frame = psobj.BaseObject as CallStackFrame;
                if (frame != null)
                {
                    callStackFrames.Add(frame);
                }
            }

            return callStackFrames.Select(c => new CallStack(c.ScriptName, c.FunctionName, c.ScriptLineNumber));
        }

        #endregion

        #region private helper

        private void SetRunspace(Runspace runspace)
        {
            if (_runspace != null)
            {
                _runspace.Debugger.DebuggerStop -= Debugger_DebuggerStop;
                _runspace.Debugger.BreakpointUpdated -= Debugger_BreakpointUpdated;
                _runspace.StateChanged -= _runspace_StateChanged;
            }

            _runspace = runspace;
            _runspace.Debugger.DebuggerStop += Debugger_DebuggerStop;
            _runspace.Debugger.BreakpointUpdated += Debugger_BreakpointUpdated;
            _runspace.StateChanged += _runspace_StateChanged;
        }

        private void RefreshScopedVariable()
        {
            ServiceCommon.Log("Debuggger stopped, let us retreive all local variable in scope");

            using (var pipeline = (_runspace.CreateNestedPipeline()))
            {
                var command = new Command("Get-Variable");
                pipeline.Commands.Add(command);
                _varaiables = pipeline.Invoke();
            }
        }

        private void RefreshCallStack()
        {
            ServiceCommon.Log("Debuggger stopped, let us retreive all call stack frames");
            using (var pipeline = (_runspace.CreateNestedPipeline()))
            {
                var command = new Command("Get-PSCallstack");
                pipeline.Commands.Add(command);
                _callstack = pipeline.Invoke();
            }
        }


        private void OnTerminatingException(Exception ex)
        {
            ServiceCommon.Log("OnTerminatingException");
            _runspace.Debugger.DebuggerStop -= Debugger_DebuggerStop;
            _runspace.Debugger.BreakpointUpdated -= Debugger_BreakpointUpdated;
            _runspace.StateChanged -= _runspace_StateChanged;
            if (_callback != null)
            {
                _callback.TerminatingException(new DebuggingServiceException(ex));
            }
        }

        private void DebuggerFinished()
        {
            ServiceCommon.Log("DebuggerFinished");
            if (_callback != null)
            {
                _callback.RefreshPrompt();
            }

            if (_runspace != null)
            {
                _runspace.Debugger.DebuggerStop -= Debugger_DebuggerStop;
                _runspace.Debugger.BreakpointUpdated -= Debugger_BreakpointUpdated;
                _runspace.StateChanged -= _runspace_StateChanged;
            }

            if (_callback != null)
            {
                _callback.DebuggerFinished();
            }
        }

        private void objects_DataAdded(object sender, DataAddedEventArgs e)
        {
            var list = sender as PSDataCollection<PSObject>;
            log += list[e.Index] + Environment.NewLine;

        }

        private void InitializeRunspace(PSHost psHost)
        {
            ServiceCommon.Log("Initializing run space with debugger");
            InitialSessionState iss = InitialSessionState.CreateDefault();
            iss.ApartmentState = ApartmentState.STA;
            iss.ThreadOptions = PSThreadOptions.ReuseThread;

            _runspace = RunspaceFactory.CreateRunspace(psHost, iss);
            _runspace.Open();

            ImportPoshToolsModule();
            LoadProfile();
        }

        private void ImportPoshToolsModule()
        {
            using (PowerShell ps = PowerShell.Create())
            {
                try
                {
                    var assemblyLocation = Assembly.GetExecutingAssembly().Location;
                    ps.Runspace = _runspace;
                    ps.AddScript("Import-Module '" + assemblyLocation + "'");
                    ps.Invoke();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to load profile.", ex);
                }
            }
        }

        private void LoadProfile()
        {
            using (PowerShell ps = PowerShell.Create())
            {
                try
                {
                    var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    var windowsPowerShell = Path.Combine(myDocuments, "WindowsPowerShell");
                    var profile = Path.Combine(windowsPowerShell, "PoshTools_profile.ps1");

                    var fi = new FileInfo(profile);
                    if (!fi.Exists)
                    {
                        return;
                    }

                    ps.Runspace = _runspace;
                    ps.AddScript(". '" + profile + "'");
                    ps.Invoke();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to load profile.", ex);
                }
            }
        }

        private void SetupExecutionPolicy()
        {
            SetExecutionPolicy(ExecutionPolicy.RemoteSigned, ExecutionPolicyScope.Process);
        }

        private void SetExecutionPolicy(ExecutionPolicy policy, ExecutionPolicyScope scope)
        {
            using (PowerShell ps = PowerShell.Create())
            {
                ps.Runspace = _runspace;
                ps.AddCommand("Set-ExecutionPolicy")
                    .AddParameter("ExecutionPolicy", policy)
                    .AddParameter("Scope", scope)
                    .AddParameter("Force");
                ps.Invoke();
            }
        }

        #endregion

    }
}
