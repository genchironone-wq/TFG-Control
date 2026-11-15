$ErrorActionPreference='Stop'
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$PSScriptRoot\TFG-Control.exe" "/resource:$PSScriptRoot\Panel.xaml,Panel.xaml" "/reference:$wpf\PresentationFramework.dll" "/reference:$wpf\PresentationCore.dll" "/reference:$wpf\WindowsBase.dll" /reference:System.Xaml.dll "$PSScriptRoot\Panel.cs"
if($LASTEXITCODE -ne 0){throw 'Build failed'}
