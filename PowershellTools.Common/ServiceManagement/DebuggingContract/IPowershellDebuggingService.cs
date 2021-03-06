﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;

namespace PowerShellTools.Common.ServiceManagement.DebuggingContract
{
    [ServiceContract(CallbackContract = typeof(IDebugEngineCallback))]
    public interface IPowershellDebuggingService
    {
        [OperationContract]
        void SetBreakpoint(PowershellBreakpoint bp);

        [OperationContract]
        void ClearBreakpoints();

        [OperationContract]
        void SetResumeAction(DebuggerResumeAction action);

        [OperationContract]
        void Execute(string cmdline);

        [OperationContract]
        void SetRunspace(bool overrideExecutionPolicy);

        [OperationContract]
        Collection<Variable> GetScopedVariable();

        [OperationContract]
        Collection<Variable> GetExpandedIEnumerableVariable(string varFullName);

        [OperationContract]
        Collection<Variable> GetPSObjectVariable(string varFullName);

        [OperationContract]
        IEnumerable<CallStack> GetCallStack();

    }
}
