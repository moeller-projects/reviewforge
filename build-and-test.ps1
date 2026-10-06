#!/usr/bin/env pwsh
param([string]$Target = ".")

# 1. Build: errors and warnings only, deduped
$out = dotnet build $Target -nologo -v q -clp:NoSummary 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { "$_".Trim() } |
    Where-Object { $_ -match '\b(error|warning) [A-Za-z]+\d+:' } |
    Sort-Object -Unique
if ($buildExit -ne 0) { Write-Host "BUILD FAILED"; exit 1 }

# 2. Test: no rebuild, TRX for parsing, coverage collection, silent console
Remove-Item TestResults -Recurse -Force -ErrorAction SilentlyContinue
dotnet test $Target --no-build -nologo -v q `
    --logger trx `
    --collect:"XPlat Code Coverage" --settings coverage.runsettings `
    --results-directory TestResults *> $null

function Load-Xml([string]$path) {
    $doc = [System.Xml.XmlDocument]::new()
    $doc.Load($path)   # literal path, safe with [ ] in filenames
    $doc
}

# 3. Parse TRX: counts + failures only
$total = 0; $passed = 0
Get-ChildItem TestResults -Filter *.trx -Recurse | ForEach-Object {
    $xml = Load-Xml $_.FullName
    $ns = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    foreach ($r in $xml.SelectNodes('//t:UnitTestResult', $ns)) {
        $total++
        if ($r.outcome -eq 'Passed') { $passed++ }
        elseif ($r.outcome -eq 'Failed') {
            $msg = $r.SelectSingleNode('.//t:Message', $ns)
            $first = if ($msg) { ($msg.InnerText.Trim() -split "`r?`n")[0] } else { '' }
            "FAIL $($r.testName): $first"
        }
    }
}
"Tests: $passed/$total passed"

# 4. Coverage: union of lines across all reports
$lines = @{}
Get-ChildItem TestResults -Filter coverage.cobertura.xml -Recurse | ForEach-Object {
    $doc = Load-Xml $_.FullName
    foreach ($cls in $doc.SelectNodes('//class')) {
        foreach ($l in $cls.SelectNodes('lines/line')) {
            $key = "$($cls.filename)|$($l.number)"
            $lines[$key] = ($lines[$key] -eq $true) -or ([int]$l.hits -gt 0)
        }
    }
}
$valid = $lines.Count
$covered = @($lines.Values | Where-Object { $_ }).Count
if ($valid) { "Line coverage: {0:N1}% ({1}/{2})" -f ($covered / $valid * 100), $covered, $valid }
else { "Line coverage: no data (is coverlet.collector installed?)" }

if ($passed -ne $total -or $total -eq 0) { exit 1 }