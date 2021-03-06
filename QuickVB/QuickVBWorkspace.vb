﻿' Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports ConsoleGUI
Imports System.IO
Imports System.Threading
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.Composition
Imports Microsoft.CodeAnalysis.MSBuild
Imports Microsoft.CodeAnalysis.Text

Public Class QuickVBWorkspace
    Inherits Workspace

    Dim _bufferMap As New Dictionary(Of DocumentId, List(Of TextBuffer))

    Public Sub New()
        MyBase.New(New MefExportPack(CompositionContainer), WorkspaceKind.Host)
    End Sub

    Function CreateProject(name As String, assemblyName As String) As ProjectId
        Dim projId = ProjectId.CreateNewId(name)
        Dim metadataReferences = {New MetadataFileReference(GetType(Object).Assembly.Location),
                                  New MetadataFileReference(GetType(Microsoft.VisualBasic.Constants).Assembly.Location),
                                  New MetadataFileReference(GetType(System.Linq.Enumerable).Assembly.Location)}
        Dim projInfo = ProjectInfo.Create(projId, VersionStamp.Default, name, assemblyName, LanguageNames.VisualBasic,
                                          metadataReferences:=metadataReferences)
        Me.OnProjectAdded(projInfo)
        Return projId
    End Function

    Sub LoadExistingSolution(solutionFileName As String)
        Dim loadedWorkSpace = MSBuildWorkspace.Create()
        Dim loadedSolution = loadedWorkSpace.OpenSolutionAsync(solutionFileName).Result

        Me.SetCurrentSolution(loadedSolution)
    End Sub

    Function CreateDocument(projId As ProjectId, name As String) As DocumentId
        Dim docId = DocumentId.CreateNewId(projId, name)
        Me.OnDocumentAdded(DocumentInfo.Create(docId, name))
        Return docId
    End Function

    ' TODO: Investigate SourceTextContainer here
    Sub RegisterTextBuffer(document As Document, buffer As TextBuffer)
        If Not _bufferMap.ContainsKey(document.Id) Then
            _bufferMap(document.Id) = New List(Of TextBuffer)
        End If

        _bufferMap(document.Id).Add(buffer)

        buffer.Text = document.GetTextAsync.Result.ToString()
    End Sub

    Sub UnregisterTextBuffer(documentId As DocumentId, buffer As TextBuffer)
        If _bufferMap.ContainsKey(documentId) Then
            _bufferMap(documentId).Remove(buffer)

            If _bufferMap(documentId).Count = 0 Then
                _bufferMap.Remove(documentId)
            End If
        End If
    End Sub

    Sub UpdateTextBuffers(document As Document)
        Dim text = document.GetTextAsync.Result.ToString()

        For Each buffer In _bufferMap(document.Id)
            buffer.Text = text
        Next
    End Sub

    Protected Overrides Sub ChangedDocumentText(documentId As DocumentId, newText As SourceText)
        Dim oldSolution = Me.CurrentSolution
        Dim newSolution = oldSolution.WithDocumentText(documentId, newText)

        Me.SetCurrentSolution(newSolution)

        UpdateTextBuffers(newSolution.GetDocument(documentId))
    End Sub

    Sub ChangedDocumentTextInternal(documentId As DocumentId, text As String)
        Dim solution = Me.CurrentSolution

        Dim textSnapshot = SourceText.From(text)
        solution = solution.WithDocumentText(documentId, textSnapshot)

        Me.SetCurrentSolution(solution)
    End Sub


    ' Compilation:

    Public Function BuildProgram() As List(Of Diagnostic)
        Return CompileProgram(False)
    End Function

    Const BinFolderName = "bin"
    Private Shared ExcludeFolderNames As String() = {BinFolderName, "obj", "QuickVB.sln.ide"}

    Public Function CompileProgram(background As Boolean, Optional cancel As CancellationToken = Nothing) As List(Of Diagnostic)
        Dim diagnostics As New List(Of Diagnostic)

        If Not background Then
            If Directory.Exists(BinFolderName) Then Directory.Delete(BinFolderName, True)
            Directory.CreateDirectory(BinFolderName)
            CopyDirectory(New DirectoryInfo("."), New DirectoryInfo(BinFolderName), ExcludeFolderNames)
        End If

        ' Builds all .DLLs before all .EXEs
        ' TODO: Proper dependency tracking here
        For Each project In Me.CurrentSolution.Projects.OrderByDescending(Function(p) p.CompilationOptions.OutputKind)
            Dim compilation = project.GetCompilationAsync(cancel).Result
            Dim projectDiagnostics = compilation.GetDiagnostics(cancel)

            If background Then
                diagnostics.AddRange(projectDiagnostics)
            Else
                Dim extension = If(project.CompilationOptions.OutputKind = OutputKind.DynamicallyLinkedLibrary, ".dll", ".exe")
                Dim path = EnsureExtension(BinFolderName & "\" & project.AssemblyName, extension)
                If File.Exists(path) Then File.Delete(path)
                Dim pdbPath = BinFolderName & "\" & project.AssemblyName & ".pdb"
                If File.Exists(pdbPath) Then File.Delete(pdbPath)

                Using stream = File.OpenWrite(path), pdbStream = File.OpenWrite(pdbPath)
                    Dim result = compilation.Emit(stream, cancellationToken:=cancel, pdbFilePath:=pdbPath, pdbStream:=pdbStream)
                    stream.Close()
                    pdbStream.Close()

                    diagnostics.AddRange(result.Diagnostics)
                End Using
            End If
        Next

        Return diagnostics.OrderByDescending(Function(diagnostic) diagnostic.Severity).ToList
    End Function

    Private Function EnsureExtension(path As String, ext As String) As String
        If path.ToLowerInvariant().EndsWith(ext.ToLowerInvariant()) Then
            Return path
        Else
            Return path + ext
        End If
    End Function

    Public Sub RunProgram(screen As Screen)
        ' TODO: Run default project rather than only .EXE project
        Dim project = (From p In Me.CurrentSolution.Projects Where p.CompilationOptions.OutputKind <> OutputKind.DynamicallyLinkedLibrary).Single()
        Dim path = EnsureExtension(BinFolderName & "\" & project.AssemblyName, ".exe")

        Dim diagnostics = BuildProgram()

        Dim errorDiagnostics = diagnostics.Where(Function(diagnostic) diagnostic.Severity = DiagnosticSeverity.Error)
        If errorDiagnostics.Any() Then Exit Sub

        Console.Clear()

        Dim info As New ProcessStartInfo(path)
        info.CreateNoWindow = False
        info.UseShellExecute = False
        info.WorkingDirectory = Directory.GetCurrentDirectory & "\" & BinFolderName
        Dim proc = Process.Start(info)
        proc.WaitForExit()

        Console.SetCursorPosition(0, Console.WindowHeight - 1)
        Console.Write("Press any key to continue")
        Console.ReadKey()

        screen.RestoreScreen()
    End Sub

    Dim _backgroundCompileCancelSource As CancellationTokenSource = Nothing

    Public Sub BackgroundCompile(onCompile As Action(Of IReadOnlyList(Of Diagnostic), CancellationToken))
        If _backgroundCompileCancelSource IsNot Nothing Then
            _backgroundCompileCancelSource.Cancel()
        End If

        Task.Run(
            Sub()
                Try
                    _backgroundCompileCancelSource = New CancellationTokenSource()
                    Dim cancel = _backgroundCompileCancelSource.Token

                    Dim diagnostics = Me.CompileProgram(True, cancel)
                    Dim backgroundDiagnostics = diagnostics.AsReadOnly()

                    MyScreen.GetScreen().Post(
                        Sub()
                            Try
                                onCompile(backgroundDiagnostics, cancel)
                            Catch e As OperationCanceledException
                                ' Let next background compilation update errors
                            End Try

                            _backgroundCompileCancelSource = Nothing
                        End Sub)
                Catch e As OperationCanceledException
                    ' Let next background compilation update errors
                End Try
            End Sub)
    End Sub
End Class
