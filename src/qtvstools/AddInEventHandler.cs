/****************************************************************************
**
** Copyright (C) 2016 The Qt Company Ltd.
** Contact: https://www.qt.io/licensing/
**
** This file is part of the Qt VS Tools.
**
** $QT_BEGIN_LICENSE:GPL-EXCEPT$
** Commercial License Usage
** Licensees holding valid commercial Qt licenses may use this file in
** accordance with the commercial license agreement provided with the
** Software or, alternatively, in accordance with the terms contained in
** a written agreement between you and The Qt Company. For licensing terms
** and conditions see https://www.qt.io/terms-conditions. For further
** information use the contact form at https://www.qt.io/contact-us.
**
** GNU General Public License Usage
** Alternatively, this file may be used under the terms of the GNU
** General Public License version 3 as published by the Free Software
** Foundation with exceptions as appearing in the file LICENSE.GPL3-EXCEPT
** included in the packaging of this file. Please review the following
** information to ensure the GNU General Public License requirements will
** be met: https://www.gnu.org/licenses/gpl-3.0.html.
**
** $QT_END_LICENSE$
**
****************************************************************************/

using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.VCProjectEngine;
using QtProjectLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace QtVsTools
{
    class AddInEventHandler
    {
        private DTE dte;
        private EnvDTE.SolutionEvents solutionEvents;
        private EnvDTE.BuildEvents buildEvents;
        private EnvDTE.DocumentEvents documentEvents;
        private EnvDTE.ProjectItemsEvents projectItemsEvents;
        private EnvDTE.vsBuildAction currentBuildAction = vsBuildAction.vsBuildActionBuild;
        private VCProjectEngineEvents vcProjectEngineEvents = null;
        private CommandEvents debugStartEvents;
        private CommandEvents debugStartWithoutDebuggingEvents;
        private System.Threading.Thread appWrapperThread = null;
        private System.Diagnostics.Process appWrapperProcess = null;
        private bool terminateEditorThread = false;
        private TcpClient client = null;
        private byte[] qtAppWrapperHelloMessage = new byte[] { 0x48, 0x45, 0x4C, 0x4C, 0x4F };
        private SimpleThreadMessenger simpleThreadMessenger = null;
        private int dispId_VCFileConfiguration_ExcludedFromBuild;
        private int dispId_VCCLCompilerTool_UsePrecompiledHeader;
        private int dispId_VCCLCompilerTool_PrecompiledHeaderThrough;
        private int dispId_VCCLCompilerTool_PreprocessorDefinitions;
        private int dispId_VCCLCompilerTool_AdditionalIncludeDirectories;

        public AddInEventHandler(DTE _dte)
        {
            simpleThreadMessenger = new SimpleThreadMessenger(this);
            dte = _dte;
            var events = dte.Events as Events2;

            buildEvents = (EnvDTE.BuildEvents) events.BuildEvents;
            buildEvents.OnBuildBegin += buildEvents_OnBuildBegin;
            buildEvents.OnBuildProjConfigBegin += OnBuildProjConfigBegin;
            buildEvents.OnBuildDone += buildEvents_OnBuildDone;

            documentEvents = (EnvDTE.DocumentEvents) events.get_DocumentEvents(null);
            documentEvents.DocumentSaved += DocumentSaved;

            projectItemsEvents = (ProjectItemsEvents) events.ProjectItemsEvents;
            projectItemsEvents.ItemAdded += ProjectItemsEvents_ItemAdded;
            projectItemsEvents.ItemRemoved += ProjectItemsEvents_ItemRemoved;
            projectItemsEvents.ItemRenamed += ProjectItemsEvents_ItemRenamed;

            solutionEvents = (SolutionEvents) events.SolutionEvents;
            solutionEvents.ProjectAdded += SolutionEvents_ProjectAdded;
            solutionEvents.ProjectRemoved += SolutionEvents_ProjectRemoved;
            solutionEvents.Opened += SolutionEvents_Opened;
            solutionEvents.AfterClosing += SolutionEvents_AfterClosing;

            const string debugCommandsGUID = "{5EFC7975-14BC-11CF-9B2B-00AA00573819}";
            debugStartEvents = events.get_CommandEvents(debugCommandsGUID, 295);
            debugStartEvents.BeforeExecute += debugStartEvents_BeforeExecute;

            debugStartWithoutDebuggingEvents = events.get_CommandEvents(debugCommandsGUID, 368);
            debugStartWithoutDebuggingEvents.BeforeExecute += debugStartWithoutDebuggingEvents_BeforeExecute;

            dispId_VCFileConfiguration_ExcludedFromBuild = GetPropertyDispId(typeof(VCFileConfiguration), "ExcludedFromBuild");
            dispId_VCCLCompilerTool_UsePrecompiledHeader = GetPropertyDispId(typeof(VCCLCompilerTool), "UsePrecompiledHeader");
            dispId_VCCLCompilerTool_PrecompiledHeaderThrough = GetPropertyDispId(typeof(VCCLCompilerTool), "PrecompiledHeaderThrough");
            dispId_VCCLCompilerTool_PreprocessorDefinitions = GetPropertyDispId(typeof(VCCLCompilerTool), "PreprocessorDefinitions");
            dispId_VCCLCompilerTool_AdditionalIncludeDirectories = GetPropertyDispId(typeof(VCCLCompilerTool), "AdditionalIncludeDirectories");
            RegisterVCProjectEngineEvents();

            if (Vsix.Instance.AppWrapperPath == null) {
                Messages.DisplayCriticalErrorMessage("QtAppWrapper can't be found in the installation directory.");
            } else {
                appWrapperProcess = new System.Diagnostics.Process();
                appWrapperProcess.StartInfo.FileName = Vsix.Instance.AppWrapperPath;
            }
            appWrapperThread = new System.Threading.Thread(new System.Threading.ThreadStart(ListenForRequests));
            appWrapperThread.Name = "QtAppWrapperListener";
            appWrapperThread.Start();
        }

        void debugStartEvents_BeforeExecute(string Guid, int ID, object CustomIn, object CustomOut, ref bool CancelDefault)
        {
            var selectedProject = HelperFunctions.GetSelectedQtProject(dte);
            if (selectedProject != null) {
                var qtProject = QtProject.Create(selectedProject);
                if (qtProject != null)
                    qtProject.SetQtEnvironment();
            }
        }

        void debugStartWithoutDebuggingEvents_BeforeExecute(string Guid, int ID, object CustomIn, object CustomOut, ref bool CancelDefault)
        {
            var selectedProject = HelperFunctions.GetSelectedQtProject(dte);
            if (selectedProject != null) {
                var qtProject = QtProject.Create(selectedProject);
                if (qtProject != null)
                    qtProject.SetQtEnvironment();
            }
        }

        private void OpenFileExternally(string fileName)
        {
            bool abortOperation;
            CheckoutFileIfNeeded(fileName, out abortOperation);
            if (abortOperation)
                return;

            var lowerCaseFileName = fileName.ToLower();
            if (lowerCaseFileName.EndsWith(".ui")) {
                Vsix.Instance.ExtLoader.loadDesigner(fileName);

                // Designer can't cope with many files in a short time.
                System.Threading.Thread.Sleep(1000);
            } else if (lowerCaseFileName.EndsWith(".ts")) {
                ExtLoader.loadLinguist(fileName);
            }
#if false
            // QRC files are directly opened, using the QRC editor.
            else if (lowerCaseFileName.EndsWith(".qrc"))
            {
                Connect.extLoader.loadQrcEditor(fileName);
            }
#endif
        }

#if DEBUG
        private void setDirectory(string dir, string value)
        {
            foreach (EnvDTE.Project project in HelperFunctions.ProjectsInSolution(dte)) {
                var vcProject = project.Object as VCProject;
                if (vcProject == null || vcProject.Files == null)
                    continue;
                var qtProject = QtProject.Create(project);
                if (qtProject == null)
                    continue;

                if (dir == "MocDir") {
                    var oldMocDir = QtVSIPSettings.GetMocDirectory(project);
                    QtVSIPSettings.SaveMocDirectory(project, value);
                    qtProject.UpdateMocSteps(oldMocDir);
                } else if (dir == "RccDir") {
                    var oldRccDir = QtVSIPSettings.GetRccDirectory(project);
                    QtVSIPSettings.SaveRccDirectory(project, value);
                    qtProject.RefreshRccSteps(oldRccDir);
                } else if (dir == "UicDir") {
                    var oldUicDir = QtVSIPSettings.GetUicDirectory(project);
                    QtVSIPSettings.SaveUicDirectory(project, value);
                    qtProject.UpdateUicSteps(oldUicDir, true);
                }
            }
        }
#endif

        private void OnQRCFileSaved(string fileName)
        {
            foreach (EnvDTE.Project project in HelperFunctions.ProjectsInSolution(dte)) {
                var vcProject = project.Object as VCProject;
                if (vcProject == null || vcProject.Files == null)
                    continue;

                var vcFile = (VCFile) ((IVCCollection) vcProject.Files).Item(fileName);
                if (vcFile == null)
                    continue;

                var qtProject = QtProject.Create(project);
                qtProject.UpdateRccStep(vcFile, null);
            }
        }

        private void CheckoutFileIfNeeded(string fileName, out bool abortOperation)
        {
            abortOperation = false;

            if (QtVSIPSettings.GetDisableCheckoutFiles())
                return;

            SourceControl sourceControl = dte.SourceControl;
            if (sourceControl == null)
                return;

            if (!sourceControl.IsItemUnderSCC(fileName))
                return;

            if (sourceControl.IsItemCheckedOut(fileName))
                return;

            if (QtVSIPSettings.GetAskBeforeCheckoutFile()) {
                var shortFileName = System.IO.Path.GetFileName(fileName);
                var dr = MessageBox.Show(
                                    SR.GetString("QuestionSCCCheckoutOnOpen", shortFileName),
                                    Resources.msgBoxCaption, MessageBoxButtons.YesNoCancel,
                                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
                if (dr == DialogResult.Cancel)
                    abortOperation = true;
                if (dr != DialogResult.Yes)
                    return;
            }

            sourceControl.CheckOutItem(fileName);
        }

        /// <summary>
        /// Dumb control to send stuff from one thread to another.
        /// </summary>
        private class SimpleThreadMessenger : Control
        {
            private AddInEventHandler addinEventHandler = null;

            public SimpleThreadMessenger(AddInEventHandler handler)
            {
                addinEventHandler = handler;
                CreateControl();
            }

            public delegate void HandleMessageFromQtAppWrapperDelegate(string message);

            /// <summary>
            /// This function handle file names in the Addin's main thread,
            /// that come from the QtAppWrapper.
            /// </summary>
            public void HandleMessageFromQtAppWrapper(string message)
            {
                if (message.ToLower().EndsWith(".qrc"))
                    addinEventHandler.OnQRCFileSaved(message);
#if DEBUG
                else if (message.StartsWith("Autotests:set")) {
                    // Messageformat from Autotests is Autotests:set<dir>:<value>
                    // where dir is MocDir, RccDir or UicDir

                    //remove Autotests:set
                    message = message.Substring(13);

                    var dir = message.Remove(6);
                    var value = message.Substring(7);

                    addinEventHandler.setDirectory(dir, value);
                }
#endif
                else
                    addinEventHandler.OpenFileExternally(message);
            }
        }

        private void ListenForRequests()
        {
            if (appWrapperProcess == null)
                return;

            SimpleThreadMessenger.HandleMessageFromQtAppWrapperDelegate handleMessageFromQtAppWrapperDelegate;
            handleMessageFromQtAppWrapperDelegate = new SimpleThreadMessenger.HandleMessageFromQtAppWrapperDelegate(simpleThreadMessenger.HandleMessageFromQtAppWrapper);

            bool firstIteration = true;
            while (!terminateEditorThread) {
                try {
                    if (!firstIteration) {
                        if (appWrapperProcess.HasExited)
                            appWrapperProcess.Close();
                    } else {
                        firstIteration = false;
                    }
                } catch { }

                appWrapperProcess.Start();

                client = new TcpClient();
                int connectionAttempts = 0;
                int appwrapperPort = 12015;
                while (!client.Connected && !terminateEditorThread && connectionAttempts < 10) {
                    try {
                        client.Connect(IPAddress.Loopback, appwrapperPort);
                        if (!client.Connected) {
                            ++connectionAttempts;
                            System.Threading.Thread.Sleep(1000);
                        }
                    } catch {
                        ++connectionAttempts;
                        System.Threading.Thread.Sleep(1000);
                    }
                }

                if (connectionAttempts >= 10) {
                    Messages.DisplayErrorMessage(SR.GetString("CouldNotConnectToAppwrapper", appwrapperPort));
                    terminateEditorThread = true;
                }

                if (terminateEditorThread) {
                    TerminateClient();
                    return;
                }

                var clientStream = client.GetStream();

                // say hello to qtappwrapper
                clientStream.Write(qtAppWrapperHelloMessage, 0, qtAppWrapperHelloMessage.Length);
                clientStream.Flush();

                byte[] message = new byte[4096];
                int bytesRead;

                while (!terminateEditorThread) {
                    try {
                        bytesRead = 0;

                        try {
                            //blocks until a client sends a message
                            bytesRead = clientStream.Read(message, 0, 4096);
                        } catch {
                            // A socket error has occured, probably because
                            // the QtAppWrapper has been terminated.
                            // Break and then try to restart the QtAppWrapper.
                            break;
                        }

                        if (bytesRead == 0) {
                            //the client has disconnected from the server
                            break;
                        }

                        //message has successfully been received
                        var encoder = new UnicodeEncoding();
                        var fullMessageString = encoder.GetString(message, 0, bytesRead);
                        var messages = fullMessageString.Split(new Char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string messageString in messages) {
                            var index = messageString.IndexOf(' ');
                            var requestedPid = Convert.ToInt32(messageString.Substring(0, index));
                            int currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                            if (requestedPid == currentPid) {
                                // Actual file opening is done in the main thread.
                                var file = messageString.Substring(index + 1);
                                simpleThreadMessenger.Invoke(handleMessageFromQtAppWrapperDelegate, new object[] { file });
                            }
                        }
                    } catch (System.Threading.ThreadAbortException) {
                        break;
                    } catch { }
                }
                TerminateClient();
            }
        }

        private void TerminateClient()
        {
            try {
                if (client != null && client.Connected) {
                    var stream = client.GetStream();
                    if (stream != null)
                        stream.Close();
                    client.Close();
                    client = null;
                }
            } catch { /* ignore */ }
        }

        public void Disconnect()
        {
            if (buildEvents != null) {
                buildEvents.OnBuildBegin -= buildEvents_OnBuildBegin;
                buildEvents.OnBuildProjConfigBegin -= OnBuildProjConfigBegin;
                buildEvents.OnBuildDone -= buildEvents_OnBuildDone;
            }

            if (documentEvents != null)
                documentEvents.DocumentSaved -= DocumentSaved;

            if (projectItemsEvents != null) {
                projectItemsEvents.ItemAdded -= ProjectItemsEvents_ItemAdded;
                projectItemsEvents.ItemRemoved -= ProjectItemsEvents_ItemRemoved;
                projectItemsEvents.ItemRenamed -= ProjectItemsEvents_ItemRenamed;
            }

            if (solutionEvents != null) {
                solutionEvents.ProjectAdded -= SolutionEvents_ProjectAdded;
                solutionEvents.ProjectRemoved -= SolutionEvents_ProjectRemoved;
                solutionEvents.Opened -= SolutionEvents_Opened;
                solutionEvents.AfterClosing -= SolutionEvents_AfterClosing;
            }

            if (debugStartEvents != null)
                debugStartEvents.BeforeExecute -= debugStartEvents_BeforeExecute;

            if (debugStartWithoutDebuggingEvents != null)
                debugStartWithoutDebuggingEvents.BeforeExecute -= debugStartWithoutDebuggingEvents_BeforeExecute;

            if (vcProjectEngineEvents != null)
                vcProjectEngineEvents.ItemPropertyChange -= OnVCProjectEngineItemPropertyChange;

            if (appWrapperThread != null) {
                terminateEditorThread = true;
                if (appWrapperThread.IsAlive) {
                    TerminateClient();
                    if (!appWrapperThread.Join(1000))
                        appWrapperThread.Abort();
                }
            }
        }

        public void OnBuildProjConfigBegin(string projectName, string projectConfig, string platform, string solutionConfig)
        {
            if (currentBuildAction != vsBuildAction.vsBuildActionBuild &&
                currentBuildAction != vsBuildAction.vsBuildActionRebuildAll) {
                return;     // Don't do anything, if we're not building.
            }

            EnvDTE.Project project = null;
            foreach (EnvDTE.Project p in HelperFunctions.ProjectsInSolution(dte)) {
                if (p.UniqueName == projectName) {
                    project = p;
                    break;
                }
            }
            if (project == null || !HelperFunctions.IsQtProject(project))
                return;

            var qtpro = QtProject.Create(project);
            var versionManager = QtVersionManager.The();
            var qtVersion = versionManager.GetProjectQtVersion(project, platform);
            if (qtVersion == null) {
                Messages.DisplayCriticalErrorMessage(SR.GetString("ProjectQtVersionNotFoundError", platform));
                dte.ExecuteCommand("Build.Cancel", "");
                return;
            }

            if (!QtVSIPSettings.GetDisableAutoMocStepsUpdate()) {
                if (qtpro.ConfigurationRowNamesChanged) {
                    qtpro.UpdateMocSteps(QtVSIPSettings.GetMocDirectory(project));
                }
            }

            // Solution config is given to function to get QTDIR property
            // set correctly also during batch build
            qtpro.SetQtEnvironment(qtVersion, solutionConfig);
            if (QtVSIPSettings.GetLUpdateOnBuild(project))
                Translation.RunlUpdate(project);
        }

        void buildEvents_OnBuildBegin(vsBuildScope Scope, vsBuildAction Action)
        {
            currentBuildAction = Action;
        }

        public void buildEvents_OnBuildDone(vsBuildScope Scope, vsBuildAction Action)
        {
        }

        public void DocumentSaved(EnvDTE.Document document)
        {
            var qtPro = QtProject.Create(document.ProjectItem.ContainingProject);

            if (!HelperFunctions.IsQtProject(qtPro.VCProject))
                return;

            var file = (VCFile) ((IVCCollection) qtPro.VCProject.Files).Item(document.FullName);

            if (file.Extension == ".ui") {
                if (QtVSIPSettings.AutoUpdateUicSteps() && !QtProject.HasUicStep(file))
                    qtPro.AddUic4BuildStep(file);
                return;
            }

            if (!HelperFunctions.HasSourceFileExtension(file.Name) && !HelperFunctions.HasHeaderFileExtension(file.Name))
                return;

            if (HelperFunctions.HasQObjectDeclaration(file)) {
                if (!qtPro.HasMocStep(file))
                    qtPro.AddMocStep(file);
            } else {
                qtPro.RemoveMocStep(file);
            }

            if (HelperFunctions.HasSourceFileExtension(file.Name)) {
                string moccedFileName = "moc_" + file.Name;

                if (qtPro.IsMoccedFileIncluded(file)) {
                    // exclude moc_foo.cpp from build
                    // Code copied here from 'GetFilesFromProject'
                    // For some reason error CS1771 was generated from function call
                    var tmpList = new System.Collections.Generic.List<VCFile>();
                    moccedFileName = HelperFunctions.NormalizeRelativeFilePath(moccedFileName);

                    var fi = new FileInfo(moccedFileName);
                    foreach (VCFile f in (IVCCollection) qtPro.VCProject.Files) {
                        if (f.Name.ToLower() == fi.Name.ToLower())
                            tmpList.Add(f);
                    }
                    foreach (VCFile moccedFile in tmpList)
                        QtProject.ExcludeFromAllBuilds(moccedFile);
                } else {
                    // make sure that moc_foo.cpp isn't excluded from build
                    // Code copied here from 'GetFilesFromProject'
                    // For some reason error CS1771 was generated from function call
                    var moccedFiles = new System.Collections.Generic.List<VCFile>();
                    moccedFileName = HelperFunctions.NormalizeRelativeFilePath(moccedFileName);

                    var fi = new FileInfo(moccedFileName);
                    foreach (VCFile f in (IVCCollection) qtPro.VCProject.Files) {
                        if (f.Name.ToLower() == fi.Name.ToLower())
                            moccedFiles.Add(f);
                    }
                    if (moccedFiles.Count > 0) {
                        var hasDifferentMocFilesPerConfig = QtVSIPSettings.HasDifferentMocFilePerConfig(qtPro.Project);
                        var hasDifferentMocFilesPerPlatform = QtVSIPSettings.HasDifferentMocFilePerPlatform(qtPro.Project);
                        var generatedFiles = qtPro.FindFilterFromGuid(Filters.GeneratedFiles().UniqueIdentifier);
                        foreach (VCFile fileInFilter in (IVCCollection) generatedFiles.Files) {
                            if (fileInFilter.Name == moccedFileName) {
                                foreach (VCFileConfiguration config in (IVCCollection) fileInFilter.FileConfigurations) {
                                    bool exclude = true;
                                    var vcConfig = config.ProjectConfiguration as VCConfiguration;
                                    if (hasDifferentMocFilesPerConfig && hasDifferentMocFilesPerPlatform) {
                                        var platform = vcConfig.Platform as VCPlatform;
                                        if (fileInFilter.RelativePath.ToLower().Contains(vcConfig.ConfigurationName.ToLower())
                                            && fileInFilter.RelativePath.ToLower().Contains(platform.Name.ToLower()))
                                            exclude = false;
                                    } else if (hasDifferentMocFilesPerConfig) {
                                        if (fileInFilter.RelativePath.ToLower().Contains(vcConfig.ConfigurationName.ToLower()))
                                            exclude = false;
                                    } else if (hasDifferentMocFilesPerPlatform) {
                                        var platform = vcConfig.Platform as VCPlatform;
                                        string platformName = platform.Name;
                                        if (fileInFilter.RelativePath.ToLower().Contains(platformName.ToLower()))
                                            exclude = false;
                                    } else {
                                        exclude = false;
                                    }
                                    if (config.ExcludedFromBuild != exclude)
                                        config.ExcludedFromBuild = exclude;
                                }
                            }
                        }
                        foreach (VCFilter filt in (IVCCollection) generatedFiles.Filters) {
                            foreach (VCFile f in (IVCCollection) filt.Files) {
                                if (f.Name == moccedFileName) {
                                    foreach (VCFileConfiguration config in (IVCCollection) f.FileConfigurations) {
                                        var vcConfig = config.ProjectConfiguration as VCConfiguration;
                                        string filterToLookFor = "";
                                        if (hasDifferentMocFilesPerConfig)
                                            filterToLookFor = vcConfig.ConfigurationName;
                                        if (hasDifferentMocFilesPerPlatform) {
                                            var platform = vcConfig.Platform as VCPlatform;
                                            if (!string.IsNullOrEmpty(filterToLookFor))
                                                filterToLookFor += '_';
                                            filterToLookFor += platform.Name;
                                        }
                                        if (filt.Name == filterToLookFor) {
                                            if (config.ExcludedFromBuild)
                                                config.ExcludedFromBuild = false;
                                        } else {
                                            if (!config.ExcludedFromBuild)
                                                config.ExcludedFromBuild = true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        public void ProjectItemsEvents_ItemAdded(ProjectItem projectItem)
        {
            var project = HelperFunctions.GetSelectedQtProject(Vsix.Instance.Dte);
            var qtPro = QtProject.Create(project);
            if (!HelperFunctions.IsQtProject(project))
                return;
            VCFilter filter = null;
            var vcFile = GetVCFileFromProject(projectItem.Name, qtPro.VCProject);
            if (vcFile == null)
                return;

            try {
                // Try to find the filter, the file is located in
                // If the file is not inside any filter, move it to
                // the according one, used by the Add-in
                filter = (VCFilter) vcFile.Parent;
            } catch { }

            try {
                var ui = Filters.FormFiles();
                var qrc = Filters.ResourceFiles();
                var ts = Filters.TranslationFiles();
                var h = Filters.HeaderFiles();
                var src = Filters.SourceFiles();

                var uiFilter = qtPro.FindFilterFromGuid(ui.UniqueIdentifier);
                var tsFilter = qtPro.FindFilterFromGuid(ts.UniqueIdentifier);
                var qrcFilter = qtPro.FindFilterFromGuid(qrc.UniqueIdentifier);
                var hFilter = qtPro.FindFilterFromGuid(h.UniqueIdentifier);
                var srcFilter = qtPro.FindFilterFromGuid(src.UniqueIdentifier);

                if (HelperFunctions.HasSourceFileExtension(vcFile.Name)) {
                    if (vcFile.Name.ToLower().StartsWith("moc_"))
                        return;
                    else if (vcFile.Name.ToLower().StartsWith("qrc_")) {
                        // Do not use precompiled headers with these files
                        QtProject.SetPCHOption(vcFile, pchOption.pchNone);
                        return;
                    }
                    var pcHeaderThrough = qtPro.GetPrecompiledHeaderThrough();
                    if (pcHeaderThrough != null) {
                        string pcHeaderCreator = pcHeaderThrough.Remove(pcHeaderThrough.LastIndexOf('.')) + ".cpp";
                        if (vcFile.Name.ToLower().EndsWith(pcHeaderCreator.ToLower())
                            && HelperFunctions.CxxFileContainsNotCommented(vcFile, "#include \"" + pcHeaderThrough + "\"", false, false)) {
                            //File is used to create precompiled headers
                            QtProject.SetPCHOption(vcFile, pchOption.pchCreateUsingSpecific);
                            return;
                        }
                    }
                    if (filter == null && !HelperFunctions.IsInFilter(vcFile, src)) {
                        if (null == srcFilter && qtPro.VCProject.CanAddFilter(src.Name)) {
                            srcFilter = (VCFilter) qtPro.VCProject.AddFilter(src.Name);
                            srcFilter.Filter = src.Filter;
                            srcFilter.ParseFiles = src.ParseFiles;
                            srcFilter.UniqueIdentifier = src.UniqueIdentifier;
                        }
                        qtPro.RemoveItem(projectItem);
                        qtPro.AddFileToProject(vcFile.FullPath, src);
                    }
                    if (HelperFunctions.HasQObjectDeclaration(vcFile)) {
                        HelperFunctions.EnsureCustomBuildToolAvailable(projectItem);
                        qtPro.AddMocStep(vcFile);
                    }
                } else if (HelperFunctions.HasHeaderFileExtension(vcFile.Name)) {
                    if (vcFile.Name.ToLower().StartsWith("ui_"))
                        return;
                    if (filter == null && !HelperFunctions.IsInFilter(vcFile, h)) {
                        if (null == hFilter && qtPro.VCProject.CanAddFilter(h.Name)) {
                            hFilter = (VCFilter) qtPro.VCProject.AddFilter(h.Name);
                            hFilter.Filter = h.Filter;
                            hFilter.ParseFiles = h.ParseFiles;
                            hFilter.UniqueIdentifier = h.UniqueIdentifier;
                        }
                        qtPro.RemoveItem(projectItem);
                        qtPro.AddFileToProject(vcFile.FullPath, h);
                    }
                    if (HelperFunctions.HasQObjectDeclaration(vcFile)) {
                        HelperFunctions.EnsureCustomBuildToolAvailable(projectItem);
                        qtPro.AddMocStep(vcFile);
                    }
                } else if (vcFile.Name.EndsWith(".ui")) {
                    if (filter == null && !HelperFunctions.IsInFilter(vcFile, ui)) {
                        if (null == uiFilter && qtPro.VCProject.CanAddFilter(ui.Name)) {
                            uiFilter = (VCFilter) qtPro.VCProject.AddFilter(ui.Name);
                            uiFilter.Filter = ui.Filter;
                            uiFilter.ParseFiles = ui.ParseFiles;
                            uiFilter.UniqueIdentifier = ui.UniqueIdentifier;
                        }
                        qtPro.RemoveItem(projectItem);
                        qtPro.AddFileToProject(vcFile.FullPath, ui);
                    }
                    HelperFunctions.EnsureCustomBuildToolAvailable(projectItem);
                    qtPro.AddUic4BuildStep(vcFile);
                } else if (vcFile.Name.EndsWith(".qrc")) {
                    if (filter == null && !HelperFunctions.IsInFilter(vcFile, qrc)) {
                        if (null == qrcFilter && qtPro.VCProject.CanAddFilter(qrc.Name)) {
                            qrcFilter = (VCFilter) qtPro.VCProject.AddFilter(qrc.Name);
                            qrcFilter.Filter = qrc.Filter;
                            qrcFilter.ParseFiles = qrc.ParseFiles;
                            qrcFilter.UniqueIdentifier = qrc.UniqueIdentifier;
                        }
                        qtPro.RemoveItem(projectItem);
                        qtPro.AddFileToProject(vcFile.FullPath, qrc);
                    }
                    HelperFunctions.EnsureCustomBuildToolAvailable(projectItem);
                    qtPro.UpdateRccStep(vcFile, null);
                } else if (HelperFunctions.IsTranslationFile(vcFile)) {
                    if (filter == null && !HelperFunctions.IsInFilter(vcFile, ts)) {
                        if (null == tsFilter && qtPro.VCProject.CanAddFilter(ts.Name)) {
                            tsFilter = (VCFilter) qtPro.VCProject.AddFilter(ts.Name);
                            tsFilter.Filter = ts.Filter;
                            tsFilter.ParseFiles = ts.ParseFiles;
                            tsFilter.UniqueIdentifier = ts.UniqueIdentifier;
                        }
                        qtPro.RemoveItem(projectItem);
                        qtPro.AddFileToProject(vcFile.FullPath, ts);
                    }
                }
            } catch { }

            return;
        }

        void ProjectItemsEvents_ItemRemoved(ProjectItem ProjectItem)
        {
            var pro = HelperFunctions.GetSelectedQtProject(Vsix.Instance.Dte);
            if (pro == null)
                return;

            var qtPro = QtProject.Create(pro);
            qtPro.RemoveGeneratedFiles(ProjectItem.Name);
        }

        void ProjectItemsEvents_ItemRenamed(ProjectItem ProjectItem, string OldName)
        {
            if (OldName == null)
                return;
            var pro = HelperFunctions.GetSelectedQtProject(Vsix.Instance.Dte);
            if (pro == null)
                return;

            var qtPro = QtProject.Create(pro);
            qtPro.RemoveGeneratedFiles(OldName);
            ProjectItemsEvents_ItemAdded(ProjectItem);
        }

        void SolutionEvents_ProjectAdded(Project project)
        {
            if (HelperFunctions.IsQMakeProject(project)) {
                RegisterVCProjectEngineEvents(project);
                var vcpro = project.Object as VCProject;
                VCFilter filter = null;
                foreach (VCFilter f in vcpro.Filters as IVCCollection)
                    if (f.Name == Filters.HeaderFiles().Name) {
                        filter = f;
                        break;
                    }
                if (filter != null) {
                    foreach (VCFile file in filter.Files as IVCCollection) {
                        foreach (VCFileConfiguration config in file.FileConfigurations as IVCCollection) {
                            var tool = HelperFunctions.GetCustomBuildTool(config);
                            if (tool != null && tool.CommandLine != null && tool.CommandLine.Contains("moc.exe")) {
                                var reg = new Regex("[^ ^\n]+moc\\.exe");
                                var matches = reg.Matches(tool.CommandLine);
                                string qtDir = null;
                                if (matches.Count != 1) {
                                    var vm = QtVersionManager.The();
                                    qtDir = vm.GetInstallPath(vm.GetDefaultVersion());
                                } else {
                                    qtDir = matches[0].ToString();
                                    qtDir = qtDir.Remove(qtDir.LastIndexOf("\\"));
                                    qtDir = qtDir.Remove(qtDir.LastIndexOf("\\"));
                                }
                                qtDir = qtDir.Replace("_(QTDIR)", "$(QTDIR)");
                                HelperFunctions.SetDebuggingEnvironment(project, "PATH=" + qtDir + "\\bin;$(PATH)", false);
                            }
                        }
                    }
                }
            }
        }

        void SolutionEvents_ProjectRemoved(Project project)
        {
        }

        void SolutionEvents_Opened()
        {
            foreach (Project p in HelperFunctions.ProjectsInSolution(Vsix.Instance.Dte)) {
                if (HelperFunctions.IsQtProject(p)) {
                    RegisterVCProjectEngineEvents(p);
                }
            }
        }

        void SolutionEvents_AfterClosing()
        {
            QtProject.ClearInstances();
        }

        /// <summary>
        /// Tries to get a VCProjectEngine from the loaded projects and registers the handlers for VCProjectEngineEvents.
        /// </summary>
        void RegisterVCProjectEngineEvents()
        {
            foreach (EnvDTE.Project project in HelperFunctions.ProjectsInSolution(dte))
                if (project != null && HelperFunctions.IsQtProject(project))
                    RegisterVCProjectEngineEvents(project);
        }

        /// <summary>
        /// Retrieves the VCProjectEngine from the given project and registers the handlers for VCProjectEngineEvents.
        /// </summary>
        void RegisterVCProjectEngineEvents(Project p)
        {
            if (vcProjectEngineEvents != null)
                return;

            var vcPrj = p.Object as VCProject;
            var prjEngine = vcPrj.VCProjectEngine as VCProjectEngine;
            if (prjEngine != null) {
                vcProjectEngineEvents = prjEngine.Events as VCProjectEngineEvents;
                if (vcProjectEngineEvents != null) {
                    try {
                        vcProjectEngineEvents.ItemPropertyChange += OnVCProjectEngineItemPropertyChange;
                    } catch {
                        Messages.DisplayErrorMessage("VCProjectEngine events could not be registered.");
                    }
                }
            }
        }

        private void OnVCProjectEngineItemPropertyChange(object item, object tool, int dispid)
        {
            //System.Diagnostics.Debug.WriteLine("OnVCProjectEngineItemPropertyChange " + dispid.ToString());
            var vcFileCfg = item as VCFileConfiguration;
            if (vcFileCfg == null) {
                // A global or project specific property has changed.

                var vcCfg = item as VCConfiguration;
                if (vcCfg == null)
                    return;
                var vcPrj = vcCfg.project as VCProject;
                if (vcPrj == null)
                    return;
                if (!HelperFunctions.IsQtProject(vcPrj))
                    return;

                if (dispid == dispId_VCCLCompilerTool_UsePrecompiledHeader
                    || dispid == dispId_VCCLCompilerTool_PrecompiledHeaderThrough
                    || dispid == dispId_VCCLCompilerTool_AdditionalIncludeDirectories
                    || dispid == dispId_VCCLCompilerTool_PreprocessorDefinitions) {
                    var qtPrj = QtProject.Create(vcPrj);
                    qtPrj.RefreshMocSteps();
                }
            } else {
                // A file specific property has changed.

                var vcFile = vcFileCfg.File as VCFile;
                if (vcFile == null)
                    return;
                var vcPrj = vcFile.project as VCProject;
                if (vcPrj == null)
                    return;
                if (!HelperFunctions.IsQtProject(vcPrj))
                    return;

                if (dispid == dispId_VCFileConfiguration_ExcludedFromBuild) {
                    var qtPrj = QtProject.Create(vcPrj);
                    qtPrj.OnExcludedFromBuildChanged(vcFile, vcFileCfg);
                } else if (dispid == dispId_VCCLCompilerTool_UsePrecompiledHeader
                      || dispid == dispId_VCCLCompilerTool_PrecompiledHeaderThrough
                      || dispid == dispId_VCCLCompilerTool_AdditionalIncludeDirectories
                      || dispid == dispId_VCCLCompilerTool_PreprocessorDefinitions) {
                    var qtPrj = QtProject.Create(vcPrj);
                    qtPrj.RefreshMocStep(vcFile);
                }
            }
        }

        private static VCFile GetVCFileFromProject(string absFileName, VCProject project)
        {
            foreach (VCFile f in (IVCCollection) project.Files) {
                if (f.Name.ToLower() == absFileName.ToLower())
                    return f;
            }
            return null;
        }

        /// <summary>
        /// Returns the COM DISPID of the given property.
        /// </summary>
        private static int GetPropertyDispId(Type type, string propertyName)
        {
            var pi = type.GetProperty(propertyName);
            if (pi != null) {
                foreach (Attribute attribute in pi.GetCustomAttributes(true)) {
                    var dispIdAttribute = attribute as DispIdAttribute;
                    if (dispIdAttribute != null) {
                        return dispIdAttribute.Value;
                    }
                }
            }
            return 0;
        }

    }
}
