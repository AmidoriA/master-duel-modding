#Requires -Version 5.1
<#
.SYNOPSIS
  Drive one running Floowan.Desktop instance through Windows UI Automation (no screenshots).

.DESCRIPTION
  Every action targets a single process id, so other copies of the app (same window title)
  are never touched. Elements are found by AutomationId, which WPF sets from x:Name.
  Run with Windows PowerShell 5.1:
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/ui-test/floowan-ui.ps1 -ProcessId <pid> -Action <action> ...

  Actions:
    windows                        List this process's top-level and owned windows.
    tab       -Id <TabItem>        Select a tab (e.g. OverFrameTab, ToolsTab).
    text      -Id <id>             Print an element's Name (TextBlock text, button caption).
    settext   -Id <id> -Value <s>  Set a TextBox (e.g. OfSearchBox) via ValuePattern.
    selectfirst -Id <ListBox>      Select the first item of a list (e.g. OfCardList).
    expand    -Id <Expander>       Expand an Expander (e.g. OfAdvancedExpander).
    invoke    -Id <Button>         Press a button. Runs on a background runspace so a button that
                                   opens a modal dialog does not block this script.
    combo     -Id <ComboBox>       Print the selected item's AutomationId.
    pick      -Id <ComboBox> -Value <ComboBoxItem id>   Select a combo item (fires SelectionChanged).
    toggle    -Id <CheckBox>       Print ToggleState.
    click     -Id <id>             Real mouse click on the element: brings its window to the
                                   front, moves the cursor, clicks. Needed for RadioButtons
                                   (UIA Select does not raise Click) and anything else that only
                                   reacts to a real click.
    close                          Close the window given by -Window (WindowPattern.Close).

  -Window selects which window to search: a title prefix (default: the main window,
  "Masterduel modding tool"). Owned dialogs such as "Custom overframe art" are found too.

.EXAMPLE
  # Open Custom OF for Effect Veiler in the dev build (pid 22088) and read its model picker.
  $ui = "scripts/ui-test/floowan-ui.ps1"
  & $ui -ProcessId 22088 -Action tab -Id OverFrameTab
  & $ui -ProcessId 22088 -Action settext -Id OfSearchBox -Value "Effect Veiler"
  & $ui -ProcessId 22088 -Action selectfirst -Id OfCardList
  & $ui -ProcessId 22088 -Action expand -Id OfAdvancedExpander
  & $ui -ProcessId 22088 -Action invoke -Id OfCustomOverframeButton
  & $ui -ProcessId 22088 -Action combo -Id CutoutModelBox -Window "Custom overframe art"
  & $ui -ProcessId 22088 -Action click -Id SubjectAutoRadio -Window "Custom overframe art"
  & $ui -ProcessId 22088 -Action text -Id StatusText -Window "Custom overframe art"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [int] $ProcessId,
    [Parameter(Mandatory = $true)]
    [ValidateSet('windows', 'tab', 'text', 'settext', 'selectfirst', 'expand', 'invoke', 'combo', 'pick', 'toggle', 'click', 'close')]
    [string] $Action,
    [string] $Id,
    [string] $Value,
    [string] $Window = 'Masterduel modding tool',
    [int] $WaitMs = 600
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class FloowanUiNative {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
'@

$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$AnyCondition = [System.Windows.Automation.Condition]::TrueCondition

function Get-ProcessWindows {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $ProcessId)
    foreach ($top in $AE::RootElement.FindAll($Scope::Children, $cond)) {
        $top
        foreach ($child in $top.FindAll($Scope::Children, $AnyCondition)) {
            if ($child.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { $child }
        }
    }
}

function Get-TargetWindow {
    $w = Get-ProcessWindows | Where-Object { $_.Current.Name.StartsWith($Window) } | Select-Object -First 1
    if (-not $w) { throw "No window starting with '$Window' in process $ProcessId." }
    return $w
}

function Find-ById($root, [string] $automationId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $automationId)
    $el = $root.FindFirst($Scope::Descendants, $cond)
    if (-not $el) { throw "Element '$automationId' not found in '$($root.Current.Name)'." }
    return $el
}

function Get-Pattern($el, $pattern) { return $el.GetCurrentPattern($pattern) }

function Require-Id { if (-not $Id) { throw "-Id is required for -Action $Action." } }

if ($Action -eq 'windows') {
    Get-ProcessWindows | ForEach-Object {
        $r = $_.Current.BoundingRectangle
        "{0} | {1},{2} {3}x{4}" -f $_.Current.Name, $r.X, $r.Y, $r.Width, $r.Height
    }
    return
}

$win = Get-TargetWindow

switch ($Action) {
    'tab' {
        Require-Id
        (Get-Pattern (Find-ById $win $Id) ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    }
    'text' {
        Require-Id
        (Find-ById $win $Id).Current.Name
    }
    'settext' {
        Require-Id
        (Get-Pattern (Find-ById $win $Id) ([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Value)
    }
    'selectfirst' {
        Require-Id
        $itemCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $first = (Find-ById $win $Id).FindFirst($Scope::Children, $itemCond)
        if (-not $first) { throw "List '$Id' has no items." }
        (Get-Pattern $first ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
        # Data-bound items expose their model type as Name, so report the action only.
        "Selected the first item of $Id"
    }
    'expand' {
        Require-Id
        (Get-Pattern (Find-ById $win $Id) ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    }
    'invoke' {
        Require-Id
        $button = Find-ById $win $Id
        $ps = [PowerShell]::Create().AddScript({
            param($b) $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        }).AddArgument($button)
        $null = $ps.BeginInvoke()
    }
    'combo' {
        Require-Id
        $sel = (Get-Pattern (Find-ById $win $Id) ([System.Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()
        if ($sel.Count -eq 0) { '(none)' } else { $sel[0].Current.AutomationId }
    }
    'pick' {
        Require-Id
        $combo = Find-ById $win $Id
        $ec = Get-Pattern $combo ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $ec.Expand(); Start-Sleep -Milliseconds 300
        (Get-Pattern (Find-ById $combo $Value) ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
        Start-Sleep -Milliseconds 300
        try { $ec.Collapse() } catch { }
    }
    'toggle' {
        Require-Id
        (Get-Pattern (Find-ById $win $Id) ([System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState
    }
    'click' {
        Require-Id
        $el = Find-ById $win $Id
        $hwnd = [IntPtr]$win.Current.NativeWindowHandle
        [FloowanUiNative]::ShowWindow($hwnd, 9) | Out-Null
        # A synthetic Alt press lets SetForegroundWindow succeed from a background process.
        [FloowanUiNative]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        [FloowanUiNative]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        [FloowanUiNative]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 300
        $r = $el.Current.BoundingRectangle
        $x = [int]($r.X + [Math]::Min(60, $r.Width / 2)); $y = [int]($r.Y + [Math]::Min(20, $r.Height / 2))
        [FloowanUiNative]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 100
        [FloowanUiNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        [FloowanUiNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        "Clicked $Id at $x,$y (physical pixels)"
    }
    'close' {
        (Get-Pattern $win ([System.Windows.Automation.WindowPattern]::Pattern)).Close()
    }
}

Start-Sleep -Milliseconds $WaitMs
