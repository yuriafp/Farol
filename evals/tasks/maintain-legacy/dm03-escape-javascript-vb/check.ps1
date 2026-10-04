# Loads the DotNetNuke.WebUtility the run built (Windows PowerShell runs on .NET Framework, like DNN) and calls
# ClientAPI.EscapeForJavascript on a string with every character the task names.
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path (Get-Location) 'DNN Platform\DotNetNuke.WebUtility\bin\DotNetNuke.WebUtility.dll')

$text = 'a"b' + "'" + 'c\d' + "`r`n" + 'e'
$expected = 'a\"b\' + "'" + 'c\\d\r\ne'
$actual = [DotNetNuke.UI.Utilities.ClientAPI]::EscapeForJavascript($text)
if ($actual -ne $expected) {
    Write-Output "expected: $expected"
    Write-Output "actual:   $actual"
    exit 1
}

Write-Output "EscapeForJavascript returns $actual"
