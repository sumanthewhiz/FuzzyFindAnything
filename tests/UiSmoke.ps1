[CmdletBinding()]
param(
    [switch]$Enhanced,
    [switch]$Diagnose,
    [string]$ApplicationDirectory,
    [string]$ModelCache,
    [string]$EmbeddingModelCache,
    [string]$ImageModelCache,
    [string]$RegionImage,
    [string]$ProviderConfiguration
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $ApplicationDirectory) { $ApplicationDirectory = Join-Path $root 'artifacts\FuzzyFindAnything' }
$app = Join-Path $ApplicationDirectory 'FuzzyFindAnything.exe'
$fixtureTool = Join-Path $root 'tests\FuzzyFindAnything.NativeTests\bin\Release\net10.0-windows\win-x64\FuzzyFindAnything.NativeTests.exe'
$work = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('ffa-ui-' + [Guid]::NewGuid().ToString('N'))))
$data = Join-Path $work 'index'
$corpus = Join-Path $work 'files'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies @(
    [System.Windows.Automation.AutomationElement].Assembly.Location,
    [System.Windows.Automation.AutomationPattern].Assembly.Location,
    [System.Drawing.Bitmap].Assembly.Location,
    'System.Core'
) -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
public static class FfaUiChecks {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rectangle);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr process);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError=true)] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int maximum);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    private delegate bool EnumWindow(IntPtr hwnd, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr state);
    public static IntPtr FindWindow(int processId, bool tray) {
        var matches = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((hwnd, state) => {
            uint owner; GetWindowThreadProcessId(hwnd,out owner);
            if(owner != processId) return true;
            var name = new System.Text.StringBuilder(256);
            if(tray) {
                GetClassName(hwnd,name,name.Capacity);
                if(!name.ToString().StartsWith("WindowsForms10.Window.")) return true;
                name.Clear(); GetWindowText(hwnd,name,name.Capacity);
                if(name.Length != 0) return true;
            } else {
                GetWindowText(hwnd,name,name.Capacity);
                if(name.ToString() != "Fuzzy Find Anything") return true;
            }
            matches.Add(hwnd);
            return true;
        },IntPtr.Zero);
        if(matches.Count != 1) throw new Exception("Expected one " + (tray ? "tray callback" : "main") + " window; found " + matches.Count);
        return matches[0];
    }
    public static void TrayClick(IntPtr hwnd, bool right) {
        // Deliver the .NET NotifyIcon shell callback on the private desktop, which has no Explorer taskbar.
        if(!PostMessage(hwnd,0x800,IntPtr.Zero,new IntPtr(right ? 0x205 : 0x202)))
            throw new Exception("Could not deliver the tray callback: " + Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
    public static Task InvokeAsync(AutomationElement element) {
        return Task.Run(() => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
    }
    public static void AcceptFolderDialog(AutomationElement dialog, AutomationElement button) {
        var parent = new IntPtr(dialog.Current.NativeWindowHandle);
        var child = new IntPtr(button.Current.NativeWindowHandle);
        int identifier = GetDlgCtrlID(child);
        if(parent == IntPtr.Zero || child == IntPtr.Zero || identifier != 1)
            throw new Exception("The native folder confirmation button could not be identified.");
        // Focus commits the edited path; the UIA proxy's global SendInput cannot target a private desktop.
        uint current = GetCurrentThreadId(), target = GetWindowThreadProcessId(parent,IntPtr.Zero);
        if(!AttachThreadInput(current,target,true))
            throw new Exception("Could not attach to the test dialog input queue: " + Marshal.GetLastWin32Error());
        try {
            SetFocus(child);
            if(!PostMessage(child,0xF5,IntPtr.Zero,IntPtr.Zero))
                throw new Exception("Could not confirm the native folder dialog: " + Marshal.GetLastWin32Error());
        } finally { AttachThreadInput(current,target,false); }
    }
    public static void Capture(IntPtr hwnd, string path) {
        ShowWindow(hwnd,3);
        SetForegroundWindow(hwnd);
        RedrawWindow(hwnd,IntPtr.Zero,IntPtr.Zero,0x185);
        Thread.Sleep(600);
        Rect rect; GetWindowRect(hwnd,out rect);
        using(var bitmap=new Bitmap(rect.Right-rect.Left,rect.Bottom-rect.Top))
        using(var graphics=Graphics.FromImage(bitmap)) {
            var dc=graphics.GetHdc();
            try { if(!PrintWindow(hwnd,dc,2)) throw new Exception("PrintWindow failed."); }
            finally { graphics.ReleaseHdc(dc); }
            bitmap.Save(path,ImageFormat.Png);
        }
    }
}
'@

function Find-Id([string]$id) {
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}

function Wait-Check([scriptblock]$test, [string]$label, [int]$seconds = 90) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "Application exited while waiting for $label, code $($process.ExitCode)" }
        if (& $test) { return }
        Start-Sleep -Milliseconds 250
    }
    foreach ($id in @('SearchInput','EmptyResultsTitle','IndexCounts','EngineState','IndexingActivity','ErrorBanner','LogDirectory','LoggingToggle')) {
        $element = Find-Id $id
        if ($element) {
            Write-Output "$id | enabled=$($element.Current.IsEnabled) | $($element.Current.Name)"
            $pattern = $null
            if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
                Write-Output "  Value: $($pattern.Current.Value)"
            }
        }
    }
    if (Test-Path (Join-Path $data 'settings.xml')) { Get-Content -LiteralPath (Join-Path $data 'settings.xml') }
    if ($window) { [FfaUiChecks]::Capture([IntPtr]$window.Current.NativeWindowHandle, (Join-Path $root '.downloads\ui-failure.png')) }
    throw "Timed out waiting for $label"
}

function Select-Tab([string]$id) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $tab = Find-Id $id
        if ($tab) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $tab) { throw "Missing tab $id after waiting for the modal window to close." }
    ([System.Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
}

function Invoke-Button([string]$id) {
    $button = Find-Id $id
    if (-not $button) { throw "Missing button $id" }
    ([System.Windows.Automation.InvokePattern]$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Toggle-Control([string]$id) {
    $control = Find-Id $id
    if (-not $control) { throw "Missing toggle $id" }
    ([System.Windows.Automation.TogglePattern]$control.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
}

function Select-QueryPurpose([string]$label) {
    $profiles = Find-Id 'QueryProfileChoice'
    ([System.Windows.Automation.ExpandCollapsePattern]$profiles.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    $choices = $profiles.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true)))
    $choice = @($choices | Where-Object { $_.Current.Name -eq $label }) | Select-Object -First 1
    if (-not $choice) { throw "Missing approved query purpose: $label" }
    ([System.Windows.Automation.SelectionItemPattern]$choice.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    ([System.Windows.Automation.ExpandCollapsePattern]$profiles.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
}

function Read-QueryPurpose {
    $profiles = Find-Id 'QueryProfileChoice'
    $selection = ([System.Windows.Automation.SelectionPattern]$profiles.GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()
    if ($selection.Length -ne 1) { return '' }
    $selection[0].Current.Name
}

function Select-ModelChoice([string]$id, [string]$value) {
    $control = Find-Id $id
    if (-not $control) { throw "Missing model selector $id" }
    ([System.Windows.Automation.ExpandCollapsePattern]$control.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    $choices = $control.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true)))
    $choice = @($choices | Where-Object { $_.Current.ItemStatus -eq $value }) | Select-Object -First 1
    if (-not $choice) { throw "Missing model choice $value in $id" }
    ([System.Windows.Automation.SelectionItemPattern]$choice.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    if ($id -like '*ModelSource') {
        Wait-Check { (Find-Id 'ModelSourceAvailability').Current.Name -eq 'Available model sources and device choices are up to date.' } 'source discovery completion' 180
    }
    Wait-Check { (Find-Id $id).Current.IsEnabled } "model choice readiness for $id" 180
    $control = Find-Id $id
    ([System.Windows.Automation.ExpandCollapsePattern]$control.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
}

function Set-SearchThreshold([string]$kind, [double]$value) {
    ([System.Windows.Automation.RangeValuePattern](Find-Id ($kind + 'MinimumSimilarity')).GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)).SetValue($value)
}

function Get-ConfiguredModel([xml]$configuration, [string]$kind) {
    $models = $configuration.FuzzyFindAnythingConfiguration.Models
    if ($configuration.FuzzyFindAnythingConfiguration.version -eq '1') { return $models.GetAttribute($kind.ToLowerInvariant()) }
    $models.SelectSingleNode($kind).GetAttribute('modelUrl')
}

function Read-FileHash([string]$path) {
    $hash = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($path)
    try { [BitConverter]::ToString($hash.ComputeHash($stream)) }
    finally { $stream.Dispose(); $hash.Dispose() }
}

function Refresh-Window {
    $script:window = [System.Windows.Automation.AutomationElement]::FromHandle([FfaUiChecks]::FindWindow($process.Id, $false))
    $script:windowPattern = [System.Windows.Automation.WindowPattern]$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
}

function Close-ToTray {
    $handle = [FfaUiChecks]::FindWindow($process.Id, $false)
    if (-not [FfaUiChecks]::PostMessage($handle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)) {
        throw 'The close message could not be delivered to the current application window.'
    }
    Wait-Check { -not [FfaUiChecks]::IsWindowVisible([FfaUiChecks]::FindWindow($process.Id, $false)) } 'close to tray' 15
}

function Minimize-ToTray {
    $handle = [FfaUiChecks]::FindWindow($process.Id, $false)
    [void][FfaUiChecks]::ShowWindow($handle, 6)
    Start-Sleep -Milliseconds 250
    $current = [FfaUiChecks]::FindWindow($process.Id, $false)
    Write-Output ('Minimize transition: handle={0}; current={1}; style=0x{2:X8}; visible={3}' -f
        $handle, $current, [FfaUiChecks]::GetWindowLong($current, -16), [FfaUiChecks]::IsWindowVisible($current))
    Wait-Check { -not [FfaUiChecks]::IsWindowVisible([FfaUiChecks]::FindWindow($process.Id, $false)) } 'minimize to tray' 15
}

function Invoke-TrayMenu([string]$name) {
    [FfaUiChecks]::TrayClick($trayHandle, $true)
    $script:trayItem = $null
    Wait-Check {
        $script:trayItem = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)),
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))))
        $script:trayItem -ne $null
    } "tray menu item $name" 15
    ([System.Windows.Automation.InvokePattern]$trayItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

$process = $null
$window = $null
try {
    New-Item -ItemType Directory -Path $work, $corpus | Out-Null
    & $fixtureTool ui-fixture $work
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the isolated UI test corpus.' }
    if ($ModelCache) {
        if ($ImageModelCache) {
            & $fixtureTool link-model-cache $ModelCache (Join-Path $data 'models')
            if ($LASTEXITCODE -ne 0) { throw 'Could not prepare image-test embedding assets.' }
        } else { Copy-Item -LiteralPath $ModelCache -Destination (Join-Path $data 'models') -Recurse }
    }
    if ($ImageModelCache) {
        & $fixtureTool link-model-cache $ImageModelCache (Join-Path $data 'models\image-understanding')
        if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the isolated image model cache.' }
        & $fixtureTool image-text-fixture (Join-Path $corpus 'Red circle.png')
        if ($LASTEXITCODE -ne 0) { throw 'Could not generate the image OCR fixture.' }
        $configuration = New-Object Xml.XmlDocument
        $configuration.LoadXml('<FuzzyFindAnythingConfiguration version="2"><Models><Text modelUrl="https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2"/><Code modelUrl="https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2"/><Image modelUrl="https://huggingface.co/microsoft/Florence-2-base-ft" mode="described"><Tasks caption="true" ocr="false"/><Embedding use="Text"/></Image></Models></FuzzyFindAnythingConfiguration>')
        $configuration.Save((Join-Path $data 'settings.xml'))
        if ($RegionImage) { Copy-Item -LiteralPath $RegionImage -Destination (Join-Path $corpus 'Region car.jpg') }
    }
    if ($EmbeddingModelCache) {
        New-Item -ItemType Directory -Force -Path (Join-Path $data 'models') | Out-Null
        Get-ChildItem -LiteralPath $EmbeddingModelCache -Directory | Copy-Item -Destination (Join-Path $data 'models') -Recurse
    }
    if ($ProviderConfiguration) {
        $configuration = New-Object Xml.XmlDocument
        $configuration.LoadXml('<FuzzyFindAnythingConfiguration version="2"><Models><Text modelUrl="https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2"/><Code modelUrl="https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2"/><Image modelUrl="https://huggingface.co/Xenova/clip-vit-base-patch32"/></Models></FuzzyFindAnythingConfiguration>')
        $configuration.Save((Join-Path $data 'settings.xml'))
    }
    $process = Start-Process -FilePath $app -ArgumentList @('--data-dir', "`"$data`"") -PassThru
    Wait-Check {
        $script:window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)),
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MainWindow')))))
        $script:window -ne $null
    } 'main window' 30
    $mainHandle = [IntPtr]$window.Current.NativeWindowHandle
    $trayHandle = [FfaUiChecks]::FindWindow($process.Id, $true)
    $windowPattern = [System.Windows.Automation.WindowPattern]$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    if ($windowPattern.Current.WindowVisualState -ne [System.Windows.Automation.WindowVisualState]::Maximized) {
        throw 'The application did not open maximized.'
    }
    Write-Output 'PASS native WPF opens maximized'
    if ((Find-Id 'Attribution').Current.Name -ne ([char]0xA9 + ' 2026 Suman Ghosh') -or
        (Find-Id 'LinkedInLink').Current.HelpText -ne 'https://www.linkedin.com/in/ghoshsuman/' -or
        (Find-Id 'GitHubLink').Current.HelpText -ne 'https://github.com/sumanthewhiz') {
        throw 'The attribution or profile links are incorrect.'
    }
    foreach ($id in @('LinkedInLink','GitHubLink')) {
        $pattern = $null
        if (-not (Find-Id $id).TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
            throw "Profile link $id is not accessible as an actionable button."
        }
    }
    Write-Output 'PASS accessible attribution and profile links'
    if ($Diagnose) {
        Start-Sleep -Seconds 120
        $items = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($item in $items) {
            $current = $item.Current
            if ($current.Name -or $current.AutomationId) {
                Write-Output "$($current.ControlType.ProgrammaticName) | $($current.AutomationId) | enabled=$($current.IsEnabled) | $($current.Name)"
            }
        }
        [FfaUiChecks]::Capture($mainHandle, (Join-Path $root '.downloads\ui-diagnostic.png'))
        return
    }
    Wait-Check { Select-Tab 'SearchTab'; (Find-Id 'SearchButton').Current.IsEnabled } 'model ready' 360
    if ($ProviderConfiguration) {
        $providerCases = Get-Content -LiteralPath $ProviderConfiguration -Raw | ConvertFrom-Json
        $selection = $providerCases[0]
        Select-Tab 'FoldersTab'
        Wait-Check { (Find-Id 'IndexCounts').Current.Name -like '3 files |*' } 'provider baseline indexing' 180
        Select-Tab 'ModelsTab'
        Wait-Check { (Find-Id 'ModelSourceAvailability').Current.Name -eq 'Available model sources and device choices are up to date.' } 'authorized source availability' 180
        foreach ($binding in @(@{Prefix='Text';Reference=$selection.text},@{Prefix='Code';Reference=$selection.code},@{Prefix='Image';Reference=$selection.image})) {
            $reference = [Uri]$binding.Reference
            if ($reference.Scheme -ne 'provider') { continue }
            $prefix = $binding.Prefix
            $parts = $reference.AbsolutePath.Trim('/').Split('/')
            Select-ModelChoice ($prefix+'ModelSource') 'microsoft'
            Wait-Check { (Find-Id ($prefix+'MicrosoftModel')).Current.IsEnabled } 'Microsoft catalog readiness' 180
            ([System.Windows.Automation.ExpandCollapsePattern](Find-Id ($prefix+'PsapiOptions')).GetCurrentPattern(
                [System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
            Invoke-Button ($prefix+'PsapiRefresh')
            Wait-Check { (Find-Id ($prefix+'PsapiRefresh')).Current.IsEnabled } 'live PSAPI version discovery' 180
            Select-ModelChoice ($prefix+'PsapiVersion') $parts[0]
            Wait-Check { (Find-Id ($prefix+'MicrosoftModel')).Current.IsEnabled } 'selected drop model catalog' 180
            $role = if ($prefix -eq 'Text') { 'text' } else { $prefix.ToLowerInvariant() }
            $options = $selection.($role + '_options')
            $device = if ($options -and $options.device -and $options.device -ne 'Automatic') { $options.device } else { 'CPU' }
            Select-ModelChoice ($prefix+'MicrosoftModel') ($parts[1] + '|' + $device)
        }
        if ((Find-Id 'CodeModelUrl') -or (Find-Id 'ImageModelUrl') -or (Find-Id 'ImagePipelineOptions')) {
            throw 'Microsoft direct models leaked Hugging Face or image-generation controls.'
        }
        Wait-Check { (Find-Id 'ApplyModelsButton').Current.IsEnabled } 'valid mixed-source selection' 90
        Invoke-Button 'ApplyModelsButton'
        Wait-Check { (Find-Id 'ApplyModelsButton').Current.IsEnabled -and
            (Find-Id 'ModelConfigurationStatus').Current.Name -like '*Reindexing complete*' } 'mixed provider Apply and indexing' 600
        [xml]$providerSettings = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
        foreach ($kind in @('Code','Image')) {
            $node = $providerSettings.FuzzyFindAnythingConfiguration.Models.SelectSingleNode($kind)
            $reference = [Uri]$selection.($kind.ToLowerInvariant())
            $parts = $reference.AbsolutePath.Trim('/').Split('/')
            if ($node.provider -ne 'microsoft' -or $node.version -ne $parts[0] -or $node.modelId -ne $parts[1] -or $node.modelUrl) {
                throw 'The mixed provider/source/version selection was not persisted.'
            }
            $options = $selection.($kind.ToLowerInvariant() + '_options')
            $device = if ($options -and $options.device -and $options.device -ne 'Automatic') { $options.device } else { 'CPU' }
            if ($node.devicePolicy -ne $device) { throw 'The selected model/device variant was not persisted.' }
        }
        Select-Tab 'SearchTab'
        foreach ($kind in @('Document','Code','Image')) { Set-SearchThreshold $kind 0 }
        ([System.Windows.Automation.ValuePattern](Find-Id 'SearchInput').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('a red circle on a white background')
        Invoke-Button 'SearchButton'
        Wait-Check {
            $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Red circle.png'))) -ne $null
        } 'Microsoft-backed image gallery result' 90
        Toggle-Control 'ThemeToggle'
        Toggle-Control 'ThemeToggle'
        [xml]$saved = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
        if ($saved.FuzzyFindAnythingConfiguration.version -ne '4' -or $saved.FuzzyFindAnythingConfiguration.Models.Code.provider -ne 'microsoft') {
            throw 'Saving appearance lost provider settings.'
        }
        Minimize-ToTray
        [FfaUiChecks]::TrayClick($trayHandle, $false)
        Wait-Check { [FfaUiChecks]::IsWindowVisible([FfaUiChecks]::FindWindow($process.Id, $false)) } 'provider-test tray restore' 15
        Refresh-Window
        Invoke-Button 'QuitButton'
        if (-not $process.WaitForExit(50000)) { throw 'The mixed-provider UI did not quit.' }
        Write-Output 'ALL NATIVE PROVIDER UI CHECKS PASSED: independent sources, live versions, fixed models, adaptive controls, Apply, retrieval and persistence'
        return
    }
    if ($ImageModelCache) {
        Select-Tab 'FoldersTab'
        $expectedFiles = if ($RegionImage) { 4 } else { 3 }
        Wait-Check { (Find-Id 'IndexCounts').Current.Name -like "$expectedFiles files |*" } 'initial described image' 360
        Select-Tab 'ModelsTab'
        Wait-Check { Find-Id 'ImagePipelineOptions' } 'model-specific image requirements' 60
        ([System.Windows.Automation.ExpandCollapsePattern](Find-Id 'ImagePipelineOptions').GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
        if (([System.Windows.Automation.TogglePattern](Find-Id 'ImageCaptionTask').GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
            throw 'The default image task was not selected automatically.'
        }
        Toggle-Control 'ImageOcrTask'
        Invoke-Button 'ApplyModelsButton'
        Wait-Check { (Find-Id 'ApplyModelsButton').Current.IsEnabled -and
            (Find-Id 'ModelConfigurationStatus').Current.Name -like '*Reindexing complete*' } 'caption plus OCR indexing' 360
        [xml]$imageSettings = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
        if ($imageSettings.FuzzyFindAnythingConfiguration.Models.Image.Tasks.ocr -ne 'true' -or
            $imageSettings.FuzzyFindAnythingConfiguration.Models.Image.Embedding.use -ne 'Text') { throw 'Image task/dependency preferences were lost.' }
        Select-Tab 'SearchTab'
        Set-SearchThreshold 'Image' 0
        ([System.Windows.Automation.ValuePattern](Find-Id 'SearchInput').GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)).SetValue('HELLO SEARCH 123')
        Invoke-Button 'SearchButton'
        Wait-Check { (Find-Id 'ImageMatchEvidence') -and (Find-Id 'ImageMatchEvidence').Current.Name -eq 'OCR text' } 'OCR-backed image gallery result' 90
        if ((Find-Id 'ImageMatchEvidence').Current.HelpText -notlike '*HELLO SEARCH 123*') { throw 'The image match lost its generated-text evidence.' }
        Select-Tab 'ModelsTab'
        if ($RegionImage) {
            $regions = Find-Id 'ImageRegionTask'
            ([System.Windows.Automation.ExpandCollapsePattern]$regions.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
            $choices = $regions.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true)))
            $choice = @($choices | Where-Object { $_.Current.Name -eq 'Object labels' }) | Select-Object -First 1
            if (-not $choice) { throw 'The region task selector has no accessible Object labels choice.' }
            ([System.Windows.Automation.SelectionItemPattern]$choice.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            ([System.Windows.Automation.ExpandCollapsePattern]$regions.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
            Toggle-Control 'ImageCaptionTask'
            Toggle-Control 'ImageOcrTask'
            Invoke-Button 'ApplyModelsButton'
            Wait-Check { (Find-Id 'ApplyModelsButton').Current.IsEnabled -and
                (Find-Id 'ModelConfigurationStatus').Current.Name -match 'Reindexing (complete|finished)' } 'region-only image indexing' 360
            [xml]$regionSettings = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
            if ($regionSettings.FuzzyFindAnythingConfiguration.Models.Image.Tasks.regions -ne 'objects' -or
                $regionSettings.FuzzyFindAnythingConfiguration.Models.Image.Tasks.caption -ne 'false') { throw 'Region-only selection was not retained.' }
            Select-Tab 'SearchTab'
            ([System.Windows.Automation.ValuePattern](Find-Id 'SearchInput').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('wheel')
            Invoke-Button 'SearchButton'
            Wait-Check {
                $gallery = Find-Id 'ImageGallery'
                $script:regionCard = $gallery.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'Region car.jpg')))
                $script:regionCard -ne $null
            } 'region-backed image match' 90
            Wait-Check {
                $evidence = $script:regionCard.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'ImageMatchEvidence')))
                $evidence -and $evidence.Current.Name -eq 'Region label' -and $evidence.Current.HelpText -like '*wheel*'
            } 'matched region evidence'
            [FfaUiChecks]::Capture($mainHandle, (Join-Path $root '.downloads\\ui-region-match.png'))
            Select-Tab 'ModelsTab'
            Write-Output 'PASS optional region-only configuration, persisted task, image retrieval and region evidence'
        }
        ([System.Windows.Automation.ValuePattern](Find-Id 'ImageModelUrl').GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)).SetValue('https://huggingface.co/Xenova/clip-vit-base-patch32')
        Wait-Check { -not (Find-Id 'ImagePipelineOptions') } 'direct-model simple configuration' 45
        Invoke-Button 'ApplyModelsButton'
        Wait-Check { (Find-Id 'ApplyModelsButton').Current.IsEnabled -and
            (Find-Id 'ModelConfigurationStatus').Current.Name -like '*Reindexing complete*' } 'direct image model restoration' 300
        [xml]$imageSettings = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
        if ($imageSettings.FuzzyFindAnythingConfiguration.Models.Image.Tasks -or
            $imageSettings.FuzzyFindAnythingConfiguration.Models.Image.Embedding) { throw 'Hidden image pipeline configuration remained active.' }
        Toggle-Control 'ThemeToggle'
        Toggle-Control 'ThemeToggle'
        Minimize-ToTray
        [FfaUiChecks]::TrayClick($trayHandle, $false)
        Wait-Check { [FfaUiChecks]::IsWindowVisible([FfaUiChecks]::FindWindow($process.Id, $false)) } 'image-test tray restore' 15
        Refresh-Window
        Invoke-Button 'QuitButton'
        if (-not $process.WaitForExit(50000)) { throw 'The image-pipeline test app did not quit.' }
        Write-Output 'ALL NATIVE IMAGE UI CHECKS PASSED: tasks, linked embedding, provenance, direct-model simplicity, themes and tray'
        return
    }
    Select-Tab 'FoldersTab'
    Wait-Check {
        Select-Tab 'FoldersTab'
        $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Indexed'))) -ne $null
    } 'configured folder indexing' 240
    Write-Output 'PASS configured folders and live indexing status'
    if ($Enhanced) {
        if ((Find-Id 'FolderIndexingDetails').Current.Name -ne '3 succeeded  |  0 failed') { throw 'Folder details are not concise outcome counts.' }
        [FfaUiChecks]::Capture($mainHandle, (Join-Path $root '.downloads\ui-folders-light.png'))
        Toggle-Control 'ThemeToggle'
        Wait-Check { (Find-Id 'ThemeToggle').Current.ItemStatus -eq 'Dark' } 'dark appearance'
        [xml]$configuration = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
        if ($configuration.FuzzyFindAnythingConfiguration.Appearance.theme -ne 'Dark') { throw 'The theme was not saved to XML.' }
        [FfaUiChecks]::Capture($mainHandle, (Join-Path $root '.downloads\ui-folders-dark.png'))
        Write-Output 'PASS light/dark switching, persisted appearance and concise folder counts'
    }

    $browse = [FfaUiChecks]::InvokeAsync((Find-Id 'AddFolderButton'))
    $dialog = $null
    Wait-Check {
        $script:dialog = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Choose a folder to index')))
        $script:dialog -ne $null
    } 'native folder picker' 15
    ([System.Windows.Automation.WindowPattern]$dialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
    if (-not $browse.Wait(5000)) { throw 'The folder picker did not close.' }
    Write-Output 'PASS native folder browser'

    Select-Tab 'SearchTab'
    foreach ($kind in @('Document','Code','Image')) {
        $value = [System.Windows.Automation.RangeValuePattern](Find-Id ($kind + 'MinimumSimilarity')).GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        if ([Math]::Abs($value.Current.Value - 0.60) -gt 0.001) { throw 'A first-time similarity is not 0.60.' }
    }
    foreach ($id in @('SearchDocuments','SearchCode','SearchImages')) {
        if (([System.Windows.Automation.TogglePattern](Find-Id $id).GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
            throw 'First launch did not select all three search types.'
        }
    }
    Set-SearchThreshold 'Document' 0.35
    Set-SearchThreshold 'Code' 0.30
    Set-SearchThreshold 'Image' 0.15
    $query = [System.Windows.Automation.ValuePattern](Find-Id 'SearchInput').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $query.SetValue('a red circle on a white background')
    Invoke-Button 'SearchButton'
    Wait-Check {
        $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Shape guide.txt'))) -ne $null
    } 'real document search result'
    Wait-Check {
        $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Red circle.png'))) -ne $null
    } 'real image search result'
    Wait-Check {
        $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Draw circle.py'))) -ne $null
    } 'real code search result'
    Write-Output 'PASS WPF-to-native-engine real document, code and image search'
    foreach ($id in @('DocumentResults','CodeResults')) {
        $preview = (Find-Id $id).FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MatchedChunkPreview')))
        if (-not $preview -or -not $preview.Current.HelpText -or $preview.Current.Name -notlike 'Matched chunk*') {
            throw "Missing full matched-chunk hover content in $id."
        }
        if ($id -eq 'CodeResults' -and $preview.Current.HelpText -notlike '*def *') { throw 'Code chunk preview lost the matched function.' }
    }
    Toggle-Control 'SearchDocuments'
    Toggle-Control 'SearchCode'
    if ((Find-Id 'SearchImages').Current.IsEnabled) { throw 'The last selected search type can be unchecked.' }
    Invoke-Button 'SearchButton'
    Wait-Check { (Find-Id 'SearchButton').Current.IsEnabled -and (Find-Id 'ImageGallery') } 'image-only search'
    if ((Find-Id 'DocumentResults') -or (Find-Id 'CodeResults')) { throw 'Unselected result sections remained visible.' }
    Toggle-Control 'SearchDocuments'
    Set-SearchThreshold 'Image' 0.9
    Invoke-Button 'SearchButton'
    Wait-Check { (Find-Id 'SearchButton').Current.IsEnabled -and (Find-Id 'DocumentResults') } 'independent document/image thresholds'
    if ($window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Red circle.png')))) {
        throw 'The image-specific high threshold was not applied.'
    }
    Toggle-Control 'SearchCode'
    Set-SearchThreshold 'Image' 0.15
    Invoke-Button 'SearchButton'
    Wait-Check { (Find-Id 'SearchButton').Current.IsEnabled -and (Find-Id 'ImageGallery') } 'restored search preferences'
    [xml]$searchSettings = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
    if ($searchSettings.FuzzyFindAnythingConfiguration.Search.Documents.minimumSimilarity -ne '0.35' -or
        $searchSettings.FuzzyFindAnythingConfiguration.Search.Code.minimumSimilarity -ne '0.3' -or
        $searchSettings.FuzzyFindAnythingConfiguration.Search.Images.minimumSimilarity -ne '0.15') {
        throw 'Independent thresholds did not persist.'
    }
    Write-Output 'PASS selected types, last-selection guardrail, separate thresholds and full chunk hover previews'
    if ($Enhanced) {
        if (-not (Find-Id 'DocumentResults') -or -not (Find-Id 'CodeResults') -or -not (Find-Id 'ImageGallery')) { throw 'Missing separated result sections.' }
        Wait-Check {
            $previews = (Find-Id 'ImageGallery').FindAll([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Image thumbnail')))
            $previews.Count -gt 0 -and $previews[0].Current.ItemStatus -eq 'Preview loaded'
        } 'actual gallery thumbnail'
        [FfaUiChecks]::Capture($mainHandle, (Join-Path $root '.downloads\ui-search.png'))
        Toggle-Control 'ThemeToggle'
        Wait-Check { (Find-Id 'ThemeToggle').Current.ItemStatus -eq 'Light' } 'light appearance'
        [FfaUiChecks]::Capture($mainHandle, (Join-Path $root '.downloads\ui-search-light.png'))
        Toggle-Control 'ThemeToggle'
        Select-Tab 'IndexInfoTab'
        if (-not (Find-Id 'IndexMetadataPanel')) { throw 'Missing index metadata panel.' }
        Wait-Check { (Find-Id 'TotalFilesValue').Current.Name -eq '3' } 'initial metadata counts'
        if ((Find-Id 'TotalDocumentsValue').Current.Name -ne '1' -or (Find-Id 'TotalCodeFilesValue').Current.Name -ne '1' -or (Find-Id 'TotalImagesValue').Current.Name -ne '1') {
            throw 'Document/code/image metadata counts are incorrect.'
        }
        if ((Find-Id 'IndexTypeValue').Current.Name -notlike 'DiskANN (*') { throw 'DiskANN version is missing.' }
        if ((Find-Id 'LastRefreshedValue').Current.Name -eq 'Not refreshed yet') { throw 'Refresh timestamp is missing.' }
        $liveFile = Join-Path $corpus 'Live update.txt'
        [IO.File]::WriteAllText($liveFile, 'A new document to verify live index statistics.', [Text.Encoding]::UTF8)
        Wait-Check { (Find-Id 'TotalFilesValue').Current.Name -eq '4' -and (Find-Id 'TotalDocumentsValue').Current.Name -eq '2' } 'live metadata update'
        [FfaUiChecks]::Capture($mainHandle, (Join-Path $root '.downloads\ui-index.png'))
        [IO.File]::Delete($liveFile)
        Wait-Check { (Find-Id 'TotalFilesValue').Current.Name -eq '3' } 'live metadata deletion'
        Write-Output 'PASS actual image thumbnail and live metadata counts, version and refresh timestamp'
    } else {
        [FfaUiChecks]::Capture($mainHandle, (Join-Path $root '.downloads\ui-search.png'))
    }
    Select-Tab 'SearchTab'
    Invoke-Button 'ClearSearchButton'
    if ($query.Current.Value -ne '' -or (Find-Id 'EmptyResultsTitle').Current.Name -ne 'Search your index') {
        throw 'Clear did not reset the query and results.'
    }
    $query.SetValue(('quarterly financial report budget forecast ' * 30))
    Invoke-Button 'SearchButton'
    if ((Find-Id 'SearchButton').Current.IsEnabled) { throw 'The cancellation check did not begin an active search.' }
    Invoke-Button 'ClearSearchButton'
    Start-Sleep -Seconds 8
    if ($query.Current.Value -ne '' -or (Find-Id 'ClearSearchButton').Current.IsEnabled -or
        (Find-Id 'EmptyResultsTitle').Current.Name -ne 'Search your index') {
        throw 'A late search response repopulated cleared results.'
    }
    Write-Output 'PASS clearing completed and in-flight searches without stale results'
    $query.SetValue('a red circle on a white background')

    if ($Enhanced) {
        Select-Tab 'ConfigurationTab'
        Wait-Check { (Find-Id 'LoggingToggle').Current.IsEnabled } 'logging controls'
        if ((Find-Id 'LoggingToggle').Current.ItemStatus -ne 'Enabled') { throw 'Logging is not enabled by default.' }
        $defaultLogs = Join-Path $data 'Logs'
        $outcomes = Get-Content -LiteralPath (Join-Path $defaultLogs 'indexer.jsonl') | ForEach-Object { $_ | ConvertFrom-Json }
        if (@($outcomes | Where-Object outcome -eq 'Succeeded').Count -lt 2) { throw 'Initial per-file success records are missing.' }
        Toggle-Control 'LoggingToggle'
        Wait-Check { (Find-Id 'LoggingToggle').Current.ItemStatus -eq 'Disabled' } 'disable logging'
        $logSizes = @{}
        Get-ChildItem -LiteralPath $defaultLogs -File | ForEach-Object { $logSizes[$_.Name] = $_.Length }
        [IO.File]::WriteAllText((Join-Path $corpus 'Not logged.txt'), 'A file indexed while diagnostic logging is disabled.', [Text.Encoding]::UTF8)
        Wait-Check { (Find-Id 'IndexCounts').Current.Name -eq '4 files | 4 passages' } 'indexing while logging disabled'
        foreach ($name in $logSizes.Keys) {
            if ((Get-Item -LiteralPath (Join-Path $defaultLogs $name)).Length -ne $logSizes[$name]) { throw "Disabled logging still wrote $name" }
        }
        $customLogs = Join-Path $work 'log & diagnostics'
        New-Item -ItemType Directory -Path $customLogs | Out-Null
        $picker = [FfaUiChecks]::InvokeAsync((Find-Id 'ChangeLogDirectoryButton'))
        Wait-Check {
            $script:dialog = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Choose a log folder')))
            $script:dialog -ne $null
        } 'log location picker' 15
        $edits = $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
        $folderInput = @($edits | Where-Object { $_.Current.Name -like 'Folder*' }) | Select-Object -First 1
        if (-not $folderInput) { throw ('Missing folder input: ' + (($edits | ForEach-Object { $_.Current.Name + ':' + $_.Current.AutomationId }) -join ', ')) }
        ([System.Windows.Automation.ValuePattern]$folderInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($customLogs)
        $select = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, '1')))
        if (-not $select) { throw 'The folder picker has no Select folder button.' }
        [FfaUiChecks]::AcceptFolderDialog($dialog, $select)
        Start-Sleep -Milliseconds 700
        $stillOpen = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Choose a log folder')))
        if ($stillOpen) {
            $remainingInput = [System.Windows.Automation.ValuePattern]$folderInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            Write-Output "Folder picker input after navigation: $($remainingInput.Current.Value)"
            [FfaUiChecks]::AcceptFolderDialog($dialog, $select)
        }
        if (-not $picker.Wait(10000)) { throw 'The log folder picker did not close.' }
        Select-Tab 'ConfigurationTab'
        Wait-Check {
            Select-Tab 'ConfigurationTab'
            ([System.Windows.Automation.ValuePattern](Find-Id 'LogDirectory').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq $customLogs -and
            (Find-Id 'LoggingToggle').Current.IsEnabled
        } 'custom log directory' 30
        Toggle-Control 'LoggingToggle'
        Wait-Check { (Find-Id 'LoggingToggle').Current.ItemStatus -eq 'Enabled' } 'enable custom logging'
        [IO.File]::WriteAllText((Join-Path $corpus 'Logged.txt'), 'An indexed document with a per-file log record.', [Text.Encoding]::UTF8)
        [IO.File]::WriteAllBytes((Join-Path $corpus 'Unreadable.txt'), [byte[]]@())
        Wait-Check {
            $records = Get-Content -LiteralPath (Join-Path $customLogs 'indexer.jsonl') | ForEach-Object { $_ | ConvertFrom-Json }
            @($records | Where-Object { $_.path -like '*\Logged.txt' -and $_.outcome -eq 'Succeeded' }).Count -gt 0 -and
            @($records | Where-Object { $_.path -like '*\Unreadable.txt' -and $_.outcome -eq 'Failed' -and $_.error.code -eq 'empty_file' -and $_.error.details }).Count -gt 0
        } 'success and failure records in the custom log folder'
        [FfaUiChecks]::Capture($mainHandle, (Join-Path $root '.downloads\ui-configuration-dark.png'))
        Toggle-Control 'ThemeToggle'
        [FfaUiChecks]::Capture($mainHandle, (Join-Path $root '.downloads\ui-configuration-light.png'))
        Toggle-Control 'ThemeToggle'
        Toggle-Control 'LoggingToggle'
        Wait-Check { (Find-Id 'LoggingToggle').Current.ItemStatus -eq 'Disabled' } 'persist disabled logging'
        [xml]$configuration = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
        if ($configuration.FuzzyFindAnythingConfiguration.Logging.enabled -ne 'false' -or
            $configuration.FuzzyFindAnythingConfiguration.Logging.directory -ne $customLogs) { throw 'Logging preferences were not saved to XML.' }
        Write-Output 'PASS logging defaults, disable, native folder selection, per-file outcomes and XML persistence'
    }

    Select-Tab 'ModelsTab'
    if ($Enhanced) {
        if ((Find-Id 'ModelsTab').Current.Name -ne 'Model configuration') { throw 'The model tab lost its accessible name.' }
        foreach ($theme in @('dark','light')) {
            Wait-Check { (Find-Id 'ThemeToggle').Current.ItemStatus -ieq $theme } 'model tab theme'
            $guidance = @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) |
                Where-Object { $_.Current.Name -like 'Compatible embeddings*' -or
                    $_.Current.Name -like 'A model must provide*' -or $_.Current.AutomationId -eq 'ModelConfigurationStatus' } |
                Sort-Object { $_.Current.BoundingRectangle.Top })
            if ($guidance.Count -ne 3) { throw 'The model configuration guidance is incomplete.' }
            $firstGap = $guidance[1].Current.BoundingRectangle.Top - $guidance[0].Current.BoundingRectangle.Bottom
            $secondGap = $guidance[2].Current.BoundingRectangle.Top - $guidance[1].Current.BoundingRectangle.Bottom
            if ($firstGap -le 0 -or [Math]::Abs($firstGap - $secondGap) -gt 1) { throw "Model guidance spacing is inconsistent in $theme mode." }
            Toggle-Control 'ThemeToggle'
        }
        Write-Output 'PASS model tab accessibility and uniform guidance spacing in both themes'
    }
    $defaultModel = 'https://huggingface.co/google/embeddinggemma-2'
    foreach ($id in @('TextModelUrl','CodeModelUrl','ImageModelUrl')) {
        $value = [System.Windows.Automation.ValuePattern](Find-Id $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if ($value.Current.Value -ne $defaultModel) { throw "Incorrect default in $id." }
    }
    $configHash = Read-FileHash (Join-Path $data 'settings.xml')
    Invoke-Button 'ApplyModelsButton'
    Start-Sleep -Milliseconds 250
    if ((Read-FileHash (Join-Path $data 'settings.xml')) -ne $configHash) { throw 'Unchanged model Apply rewrote configuration.' }
    $codeModel = [System.Windows.Automation.ValuePattern](Find-Id 'CodeModelUrl').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $codeModel.SetValue('https://example.com/not-a-model')
    Invoke-Button 'ApplyModelsButton'
    Wait-Check { (Find-Id 'ModelConfigurationStatus').Current.Name -like 'Model configuration was not applied:*' } 'invalid model URL rejection'
    if ((Read-FileHash (Join-Path $data 'settings.xml')) -ne $configHash) { throw 'Invalid URL changed model settings.' }
    if ($ModelCache) {
        $alternate = 'https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2'
        $codeModel.SetValue($alternate)
        Invoke-Button 'ApplyModelsButton'
        Wait-Check { (Find-Id 'ApplyModelsButton').Current.IsEnabled -and (Find-Id 'ModelConfigurationStatus').Current.Name -like 'Configuration saved*' } 'model configuration apply' 300
        Wait-Check { (Find-Id 'ModelConfigurationStatus').Current.Name -like '*Reindexing complete*' } 'model reindex completion feedback' 300
        [xml]$configuration = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
        if ((Get-ConfiguredModel $configuration 'Code') -ne $alternate) { throw 'The selected code model was not saved.' }
        $codeModel.SetValue($defaultModel)
        Invoke-Button 'ApplyModelsButton'
        Wait-Check { (Find-Id 'ApplyModelsButton').Current.IsEnabled -and (Find-Id 'ModelConfigurationStatus').Current.Name -like 'Configuration saved*' } 'default model restore' 300
        Wait-Check { (Find-Id 'ModelConfigurationStatus').Current.Name -like '*Reindexing complete*' } 'default model completion feedback' 300
        [xml]$configuration = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
        if ((Get-ConfiguredModel $configuration 'Text') -ne $defaultModel -or
            (Get-ConfiguredModel $configuration 'Code') -ne $defaultModel -or
            (Get-ConfiguredModel $configuration 'Image') -ne $defaultModel) { throw 'Model defaults were not restored.' }
    } else { $codeModel.SetValue($defaultModel) }
    Write-Output 'PASS model URL defaults, no-op Apply, invalid URL preservation and configured model changes'

    if ($EmbeddingModelCache) {
        if (Find-Id 'QueryProfileChoice') { throw 'Default Gemma configuration exposes unnecessary query/tokenizer setup.' }
        $codeModel.SetValue('https://huggingface.co/Qwen/Qwen3-Embedding-0.6B')
        Wait-Check { Find-Id 'ModelDetailsExpander' } 'automatic embedding requirements' 60
        ([System.Windows.Automation.ExpandCollapsePattern](Find-Id 'ModelDetailsExpander').GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
        Wait-Check { Find-Id 'QueryProfileChoice' } 'applicable query profiles'
        Invoke-Button 'ApplyModelsButton'
        Select-Tab 'SearchTab'
        Wait-Check { (Find-Id 'SearchButton').Current.IsEnabled } 'nonblocking model validation' 30
        Select-Tab 'ModelsTab'
        Wait-Check { (Find-Id 'ApplyModelsButton').Current.IsEnabled -and
            (Find-Id 'ModelConfigurationStatus').Current.Name -like '*Reindexing complete*' } 'native Qwen code reindex' 360
        [xml]$configuration = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
        if ($configuration.FuzzyFindAnythingConfiguration.version -notin @('2','3') -or
            $configuration.FuzzyFindAnythingConfiguration.Models.Code.modelUrl -ne 'https://huggingface.co/Qwen/Qwen3-Embedding-0.6B') {
            throw 'The extended model binding was not persisted.'
        }
        Wait-Check { Find-Id 'ModelDetailsExpander' } 'saved embedding details'
        ([System.Windows.Automation.ExpandCollapsePattern](Find-Id 'ModelDetailsExpander').GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
        $profiles = Find-Id 'QueryProfileChoice'
        ([System.Windows.Automation.ExpandCollapsePattern]$profiles.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
        $selectableProfiles = $profiles.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true)))
        $documentProfile = @($selectableProfiles | Where-Object {
            $_.Current.Name -eq 'Document search' -or $_.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Document search')))
        }) | Select-Object -First 1
        if (-not $documentProfile) { throw 'The approved document query profile is missing.' }
        ([System.Windows.Automation.SelectionItemPattern]$documentProfile.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
        ([System.Windows.Automation.ExpandCollapsePattern]$profiles.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
        Invoke-Button 'ApplyModelsButton'
        Wait-Check { (Find-Id 'ApplyModelsButton').Current.IsEnabled -and
            (Find-Id 'ModelConfigurationStatus').Current.Name -like '*existing embeddings were retained*' } 'query-only setting update' 120
        [xml]$configuration = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
        if ($configuration.FuzzyFindAnythingConfiguration.Models.Code.queryProfile -ne 'document-search-v1') {
            throw 'The query-only choice was lost.'
        }
        Start-Sleep -Milliseconds 1200
        if ((Read-QueryPurpose) -ne 'Document search') { throw 'Apply cleared the visible non-default query purpose.' }
        Select-QueryPurpose 'Code search'
        Invoke-Button 'ApplyModelsButton'
        Wait-Check { (Find-Id 'ApplyModelsButton').Current.IsEnabled -and
            (Find-Id 'ModelConfigurationStatus').Current.Name -like '*existing embeddings were retained*' } 'explicit default query purpose' 120
        Start-Sleep -Milliseconds 1200
        [xml]$configuration = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
        if ($configuration.FuzzyFindAnythingConfiguration.Models.Code.queryProfile -ne 'code-search-v1' -or
            (Read-QueryPurpose) -ne 'Code search') { throw 'Apply cleared the explicit default query purpose.' }
        $retainedConfig = Read-FileHash (Join-Path $data 'settings.xml')
        Invoke-Button 'ApplyModelsButton'
        Start-Sleep -Milliseconds 300
        if ((Read-QueryPurpose) -ne 'Code search' -or (Read-FileHash (Join-Path $data 'settings.xml')) -ne $retainedConfig) {
            throw 'No-op Apply changed the query-purpose selection.'
        }
        Write-Output 'PASS visible Query Purpose retained after Apply, refresh and no-op'
        $codeModel.SetValue($defaultModel)
        Wait-Check { -not (Find-Id 'QueryProfileChoice') } 'simple default model form'
        Invoke-Button 'ApplyModelsButton'
        Wait-Check { (Find-Id 'ApplyModelsButton').Current.IsEnabled -and
            (Find-Id 'ModelConfigurationStatus').Current.Name -like '*Reindexing complete*' } 'restored default binding' 300
        [xml]$configuration = Get-Content -LiteralPath (Join-Path $data 'settings.xml') -Raw
        if ($configuration.FuzzyFindAnythingConfiguration.Models.Code.modelUrl -ne $defaultModel -or
            $configuration.FuzzyFindAnythingConfiguration.Models.Code.queryProfile) { throw 'Hidden model-specific settings leaked into Gemma.' }
        Write-Output 'PASS progressive disclosure, native Qwen Apply, query-only settings and default simplicity'
    }

    Select-Tab 'FoldersTab'
    Wait-Check {
        $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Indexed'))) -ne $null
    } 'model reindex completion' 180
    Select-Tab 'SearchTab'
    $filesBeforeTray = [int]([regex]::Match((Find-Id 'IndexCounts').Current.Name, '^\d+').Value)
    Minimize-ToTray
    $trayFile = Join-Path $corpus 'Tray background.txt'
    [IO.File]::WriteAllText($trayFile, 'This document was indexed while the window was hidden in the system tray.', [Text.Encoding]::UTF8)
    Wait-Check {
        $found = & $fixtureTool lookup-file $data 'Tray background.txt'
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the isolated tray-test catalog.' }
        @($found | ConvertFrom-Json).Count -eq 1
    } 'indexing while hidden in the tray'
    [FfaUiChecks]::TrayClick($trayHandle, $false)
    Wait-Check { [FfaUiChecks]::IsWindowVisible([FfaUiChecks]::FindWindow($process.Id, $false)) } 'restore after background indexing' 15
    Refresh-Window
    Wait-Check { (Find-Id 'IndexCounts').Current.Name -like "$($filesBeforeTray + 1) files |*" } 'tray-indexed file count'
    [IO.File]::Delete($trayFile)
    Wait-Check { (Find-Id 'IndexCounts').Current.Name -like "$filesBeforeTray files |*" } 'tray fixture deletion'
    Write-Output 'PASS live indexing continues while hidden in the system tray'

    Select-Tab 'FoldersTab'
    $checkbox = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
    ([System.Windows.Automation.TogglePattern]$checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
    Wait-Check { (Find-Id 'RemoveFolderButton').Current.IsEnabled } 'checkbox selection'
    Invoke-Button 'RemoveFolderButton'
    Wait-Check { -not (Find-Id 'RemoveFolderButton').Current.IsEnabled } 'folder removal'
    Select-Tab 'SearchTab'
    Wait-Check { (Find-Id 'SearchButton').Current.IsEnabled -and (Find-Id 'IndexCounts').Current.Name -eq '0 files | 0 passages' } 'completed folder removal'
    Invoke-Button 'SearchButton'
    Wait-Check {
        (Find-Id 'EmptyResultsTitle').Current.Name -eq 'No matching files found.'
    } 'no-match result state'
    if (-not (Test-Path (Join-Path $corpus 'Shape guide.txt'))) { throw 'Removing a folder deleted an original file.' }
    Write-Output 'PASS checkbox removal and no-match UI; original files retained'

    Minimize-ToTray
    $hiddenHandle = [FfaUiChecks]::FindWindow($process.Id, $false)
    if (([FfaUiChecks]::GetWindowLong($hiddenHandle, -20) -band 0x40000) -ne 0) { throw 'The hidden app still has a taskbar button.' }
    [FfaUiChecks]::TrayClick($trayHandle, $false)
    Wait-Check { [FfaUiChecks]::IsWindowVisible([FfaUiChecks]::FindWindow($process.Id, $false)) } 'tray click restore' 15
    Refresh-Window
    if ($windowPattern.Current.WindowVisualState -ne [System.Windows.Automation.WindowVisualState]::Maximized) { throw 'Tray restore was not maximized.' }
    Close-ToTray
    Wait-Check { -not [FfaUiChecks]::IsWindowVisible([FfaUiChecks]::FindWindow($process.Id, $false)) } 'close to tray' 15
    Invoke-TrayMenu 'Open Fuzzy Find Anything'
    Wait-Check { [FfaUiChecks]::IsWindowVisible([FfaUiChecks]::FindWindow($process.Id, $false)) } 'tray menu restore' 15
    Refresh-Window
    Close-ToTray
    $second = Start-Process -FilePath $app -ArgumentList @('--data-dir', "`"$data`"") -PassThru
    $second.WaitForExit(15000) | Out-Null
    Refresh-Window
    Wait-Check { $windowPattern.Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Maximized } 'single-instance activation' 15
    Write-Output 'PASS minimize/close to tray, tray click/menu restore, and maximized single-instance activation'
    Select-Tab 'SearchTab'
    Toggle-Control 'SearchCode'
    Close-ToTray
    Invoke-TrayMenu 'Quit'
    if (-not $process.WaitForExit(50000)) { throw 'Quit did not stop the application.' }
    Write-Output 'PASS Quit from the tray while the application window is hidden'
    if ($Enhanced) {
        $process = Start-Process -FilePath $app -ArgumentList @('--data-dir', "`"$data`"") -PassThru
        Wait-Check {
            $script:window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                [System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.AndCondition(
                    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)),
                    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MainWindow')))))
            $script:window -ne $null
        } 'appearance restart' 30
        if ((Find-Id 'ThemeToggle').Current.ItemStatus -ne 'Dark') { throw 'The theme did not survive an application restart.' }
        Select-Tab 'ConfigurationTab'
        if ((Find-Id 'LoggingToggle').Current.ItemStatus -ne 'Disabled' -or
            ([System.Windows.Automation.ValuePattern](Find-Id 'LogDirectory').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value -ne $customLogs) {
            throw 'The logging configuration did not survive restart.'
        }
        Select-Tab 'SearchTab'
        Wait-Check { (Find-Id 'SearchButton').Current.IsEnabled } 'restart model ready' 180
        foreach ($pair in @(@('Document',0.35),@('Code',0.30),@('Image',0.15))) {
            $similarity = [System.Windows.Automation.RangeValuePattern](Find-Id ($pair[0] + 'MinimumSimilarity')).GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
            if ([Math]::Abs($similarity.Current.Value - $pair[1]) -gt 0.001) { throw 'Per-type similarity did not survive restart.' }
        }
        if (([System.Windows.Automation.TogglePattern](Find-Id 'SearchCode').GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
            throw 'Search type selection did not survive restart.'
        }
        Invoke-Button 'QuitButton'
        if (-not $process.WaitForExit(50000)) { throw 'Restarted test application did not quit.' }
        Write-Output 'PASS appearance, similarity and logging retained after a complete restart'
    }
    Write-Output 'ALL NATIVE UI CHECKS PASSED'
} finally {
    if ($process -and -not $process.HasExited) {
        try {
            if ($window) {
                $quit = Find-Id 'QuitButton'
                if ($quit) { [FfaUiChecks]::InvokeAsync($quit).Wait(5000) | Out-Null }
            }
            $process.WaitForExit(45000) | Out-Null
        } finally {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        }
    }
    if (Test-Path $work) {
        Get-ChildItem -LiteralPath $data -Filter '*.log' -Recurse -ErrorAction SilentlyContinue |
            Copy-Item -Destination (Join-Path $root '.downloads') -Force
        [IO.Directory]::Delete($work, $true)
    }
}
