// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

/// <summary>
/// Intercept module loads for assemblies we want to collect method Jit info for.
/// Each Method that gets Jitted from a ready-to-run assembly is interesting to look at.
/// For a fully r2r'd assembly, there should be no such methods, so that would be a test failure.
/// </summary>
public class ReadyToRunJittedMethods
{
    private Dictionary<int, ProcessInfo> _pidToProcess;
    private HashSet<string> _testModuleNames;
    private HashSet<string> _testFolderNames;
    private List<long> _testModuleIds = new List<long>();
    private Dictionary<long, string> _testModuleIdToName = new Dictionary<long, string>();
    private Dictionary<string, HashSet<string>> _methodsJitted = new Dictionary<string, HashSet<string>>();

    public ReadyToRunJittedMethods(TraceEventSession session, IEnumerable<ProcessInfo> processes)
    {
        _pidToProcess = new Dictionary<int, ProcessInfo>();
        _testModuleNames = new HashSet<string>();
        _testFolderNames = new HashSet<string>();

        foreach (ProcessInfo process in processes)
        {
            if (process.CollectJittedMethods)
            {
                _testFolderNames.UnionWith(process.MonitorFolders);
                _testModuleNames.UnionWith(process.MonitorModules);
            }
        }

        session.Source.Clr.LoaderModuleLoad += delegate (ModuleLoadUnloadTraceData data)
        {
            if (ShouldMonitorModule(data))
            {
                // The console & method logging is normally too noisy to be turned on by default but
                // it's sometimes useful for debugging purposes.
                // Console.WriteLine($"Tracking module {data.ModuleILFileName} with Id {data.ModuleID}");
                _testModuleIds.Add(data.ModuleID);
                _testModuleIdToName[data.ModuleID] = Path.GetFileNameWithoutExtension(data.ModuleILFileName);
            }
        };

        session.Source.Clr.MethodLoadVerbose += delegate (MethodLoadUnloadVerboseTraceData data)
        {
            ProcessInfo processInfo;
            if (data.IsJitted && _pidToProcess.TryGetValue(data.ProcessID, out processInfo) && _testModuleIds.Contains(data.ModuleID))
            {
                // Console.WriteLine($"Method loaded {GetName(data)} - {data}");
                string methodName = GetName(data);
                string moduleName = _testModuleIdToName[data.ModuleID];
                if (processInfo.JittedMethods == null)
                {
                    processInfo.JittedMethods = new Dictionary<string, HashSet<string>>();
                }
                HashSet<string> modulesForMethod;
                if (!processInfo.JittedMethods.TryGetValue(methodName, out modulesForMethod))
                {
                    modulesForMethod = new HashSet<string>();
                    processInfo.JittedMethods.Add(methodName, modulesForMethod);
                }
                modulesForMethod.Add(moduleName);
            }
        };
    }

    public void SetProcessId(ProcessInfo processInfo, int pid)
    {
        _pidToProcess[pid] = processInfo;
    }

    private bool ShouldMonitorModule(ModuleLoadUnloadTraceData data)
    {
        if (!_pidToProcess.ContainsKey(data.ProcessID))
            return false;

        if (File.Exists(data.ModuleILPath) && _testFolderNames.Contains(Path.GetDirectoryName(data.ModuleILPath).ToAbsoluteDirectoryPath().ToLower()))
            return true;

        if (_testModuleNames.Contains(data.ModuleILPath.ToLower()) || _testModuleNames.Contains(data.ModuleNativePath.ToLower()))
            return true;

        return false;
    }

    public IReadOnlyDictionary<string, HashSet<string>> JittedMethods => _methodsJitted;

    /// <summary>
    /// Returns the number of test assemblies that were loaded by the runtime
    /// </summary>
    public int AssembliesWithEventsCount => _testModuleIds.Count;

    //
    // Builds a method name from event data of the form Class.Method(arg1, arg2)
    //
    private static string GetName(MethodLoadUnloadVerboseTraceData data)
    {
        var signature = "";
        var signatureWithReturnType = data.MethodSignature;
        var openParenIndex = signatureWithReturnType.IndexOf('(');

        if (0 <= openParenIndex)
        {
            signature = signatureWithReturnType.Substring(openParenIndex);
        }

        var className = data.MethodNamespace;
        var firstBox = className.IndexOf('[');
        var lastDot = className.LastIndexOf('.', firstBox >= 0 ? firstBox : className.Length - 1);
        if (0 <= lastDot)
        {
            className = className.Substring(lastDot + 1);
        }

        var optionalSeparator = ".";
        if (className.Length == 0)
        {
            optionalSeparator = "";
        }

        return className + optionalSeparator + data.MethodName + signature;
    }
}
