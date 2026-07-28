' autorun-gui.vbs - Launch SystemAutorun GUI without any console window
' Double-click this file (or make a desktop shortcut to it)
Set sh  = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

projectRoot = fso.GetParentFolderName(WScript.ScriptFullName)
pythonw     = "C:\Python314\pythonw.exe"
If Not fso.FileExists(pythonw) Then pythonw = "pythonw.exe"
script      = projectRoot & "\gui\autorun_gui.py"

' Second arg = 0: hidden window; Third arg = False: don't wait
sh.Run """" & pythonw & """ """ & script & """", 0, False
