param(
  [int]$Port = 17891,
  [switch]$NoTopmost
)

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$ErrorActionPreference = 'Stop'
$script:RootDir = $PSScriptRoot
$script:BaseSize = 320
$script:Scale = 1.0
$script:AgentUrl = "http://127.0.0.1:$Port"
$script:StateDir = Join-Path ($env:APPDATA) 'DeepSeekWhaleOverlay'
$script:StateFile = Join-Path $script:StateDir 'state.json'
$script:RefreshJob = $null
$script:Drag = $null
$script:BubbleTimer = $null
$script:RandomBubble = $false
$script:VoiceBubbleVisible = $false
$script:RestoringState = $false
$script:PositionMargin = 12
$script:SoundOn = $true
$script:SoundVolume = 0.9
$script:VoiceEnabled = $true
$script:VoiceLoop = $false
$script:VoiceCatalog = @()
$script:SelectedVoiceKeys = @()
$script:VoiceIndex = 0
$script:VoicePlayer = $null
$script:VoicePlaying = $false
$script:VoiceMenuItems = @{}
$script:VoiceLoopItem = $null
$script:VoiceEnabledItem = $null
$script:Colors = [pscustomobject]@{
  Bubble = '#EFF5E5'
  Outline = '#26312D'
  Text = '#26312D'
  Muted = '#587063'
  Lime = '#DCEB9E'
  Pink = '#F3A5B8'
  Menu = '#F6FAEE'
}
$script:LastBalance = $null
$script:LastCurrency = 'USD'
$script:LastUsage = $null

$mutex = New-Object Threading.Mutex($false, 'Local\DeepSeekBalanceWhaleOverlay')
if (-not $mutex.WaitOne(0, $false)) { exit 0 }

function Read-State {
  if (-not (Test-Path -LiteralPath $script:StateFile)) { return $null }
  try { return Get-Content -Raw -LiteralPath $script:StateFile | ConvertFrom-Json } catch { return $null }
}

function Save-State {
  try {
    if ($null -eq $script:Window) { return }
    New-Item -ItemType Directory -Force -Path $script:StateDir | Out-Null
    [pscustomobject]@{
      version = 2
      scale = $script:Scale
      sound = $script:SoundOn
      volume = $script:SoundVolume
      voiceEnabled = $script:VoiceEnabled
      voiceLoop = $script:VoiceLoop
      selectedVoices = @($script:SelectedVoiceKeys)
      left = [double]$script:Window.Left
      top = [double]$script:Window.Top
    } | ConvertTo-Json | Set-Content -LiteralPath $script:StateFile -Encoding UTF8
  } catch {}
}

function Initialize-VoiceCatalog {
  $roots = @(
    [pscustomobject]@{ Key = 'cn'; Language = '中文'; Color = $script:Colors.Pink; Path = (Join-Path $script:RootDir 'assets\voice\cn') },
    [pscustomobject]@{ Key = 'jp'; Language = '日文'; Color = $script:Colors.Lime; Path = (Join-Path $script:RootDir 'assets\voice\jp') }
  )
  foreach ($root in $roots) {
    if (-not (Test-Path -LiteralPath $root.Path)) { continue }
    foreach ($file in (Get-ChildItem -LiteralPath $root.Path -File | Where-Object { $_.Extension -in @('.wav', '.mp3', '.m4a') } | Sort-Object Name)) {
      $script:VoiceCatalog += [pscustomobject]@{
        Key = "$($root.Key)/$($file.Name)"
        Language = $root.Language
        LanguageKey = $root.Key
        Color = $root.Color
        Name = [IO.Path]::GetFileNameWithoutExtension($file.Name)
        Path = $file.FullName
      }
    }
  }
  $script:SelectedVoiceKeys = @($script:VoiceCatalog | ForEach-Object { $_.Key })
}

function Get-SelectedVoices {
  $selected = @($script:SelectedVoiceKeys)
  return @($script:VoiceCatalog | Where-Object { $selected -contains $_.Key })
}

function Save-VoiceSelection {
  $script:SelectedVoiceKeys = @($script:VoiceMenuItems.GetEnumerator() | Where-Object { $_.Value.IsChecked } | ForEach-Object { [string]$_.Key })
  Save-State
}

function Set-VoiceSelection([string]$key, [bool]$checked) {
  if ($script:VoiceMenuItems.ContainsKey($key)) { $script:VoiceMenuItems[$key].IsChecked = $checked }
  Save-VoiceSelection
}

function Set-VoiceSelectionGroup([string]$languageKey, [bool]$checked) {
  foreach ($voice in @($script:VoiceCatalog | Where-Object { $_.LanguageKey -eq $languageKey })) {
    if ($script:VoiceMenuItems.ContainsKey($voice.Key)) { $script:VoiceMenuItems[$voice.Key].IsChecked = $checked }
  }
  Save-VoiceSelection
}

function Stop-VoicePlayback {
  $player = $script:VoicePlayer
  $script:VoicePlayer = $null
  $script:VoicePlaying = $false
  if ($player) {
    try { $player.Stop() } catch {}
    try { $player.Close() } catch {}
  }
}

function Voice-FontSize([string]$text) {
  $length = if ($text) { $text.Length } else { 0 }
  if ($length -le 8) { return 23 }
  if ($length -le 14) { return 19 }
  if ($length -le 22) { return 16 }
  if ($length -le 34) { return 13 }
  return 11
}

function Show-BalanceDisplay {
  $script:VoiceBubbleVisible = $false
  $script:LabelText.Text = 'ergouzi余额'
  $script:LabelText.FontSize = 18
  $script:LabelText.Foreground = New-Brush $script:Colors.Muted
  $script:AmountText.FontSize = 32
  $script:AmountText.FontWeight = 'Bold'
  $script:AmountText.TextWrapping = 'NoWrap'
  $script:AmountText.MaxHeight = [double]::PositiveInfinity
  $script:AmountText.Foreground = New-Brush $script:Colors.Text
  $script:AmountText.Text = if ($script:LastBalance -ne $null) { Format-Money $script:LastBalance $script:LastCurrency } else { '...' }
  $script:HintText.FontSize = 13
  $script:HintText.Foreground = New-Brush $script:Colors.Muted
  $script:HintText.Text = if ($script:LastBalance -ne $null) { '点击播放角色语音' } else { '加载中...' }
}

function Show-VoiceBubble([object]$voice) {
  if (-not $voice) { return }
  $script:VoiceBubbleVisible = $true
  $script:Bubble.Visibility = 'Visible'
  $script:LabelText.Text = "$($voice.Language)语音"
  $script:LabelText.FontSize = 16
  $script:LabelText.Foreground = New-Brush $voice.Color
  $script:AmountText.Text = $voice.Name
  $script:AmountText.FontSize = Voice-FontSize $voice.Name
  $script:AmountText.FontWeight = 'SemiBold'
  $script:AmountText.TextWrapping = 'Wrap'
  $script:AmountText.MaxHeight = 86
  $script:AmountText.Foreground = New-Brush $script:Colors.Text
  $script:HintText.Text = '点击切换语音'
  $script:HintText.FontSize = 12
  $script:HintText.Foreground = New-Brush $script:Colors.Muted
  if ($script:BubbleTimer) { $script:BubbleTimer.Stop() }
  $script:BubbleTimer = New-Object Windows.Threading.DispatcherTimer
  $script:BubbleTimer.Interval = [TimeSpan]::FromSeconds(20)
  $script:BubbleTimer.Add_Tick({ $script:Bubble.Visibility = 'Collapsed'; $script:VoiceBubbleVisible = $false; $script:BubbleTimer.Stop() })
  $script:BubbleTimer.Start()
}

function Set-WindowPosition([object]$state, [object]$workArea) {
  $left = $null
  $top = $null
  if ($state -and $state.left -ne $null -and $state.top -ne $null) {
    try {
      $candidateLeft = [double]$state.left
      $candidateTop = [double]$state.top
      if (-not [double]::IsNaN($candidateLeft) -and -not [double]::IsInfinity($candidateLeft) -and
          -not [double]::IsNaN($candidateTop) -and -not [double]::IsInfinity($candidateTop)) {
        $left = $candidateLeft
        $top = $candidateTop
      }
    } catch {}
  }
  if ($null -eq $left -or $null -eq $top) {
    $left = [double]$workArea.Right - $script:Window.Width - $script:PositionMargin
    $top = [double]$workArea.Bottom - $script:Window.Height - $script:PositionMargin
  }
  $minLeft = [double]$workArea.Left
  $minTop = [double]$workArea.Top
  $maxLeft = [math]::Max($minLeft, [double]$workArea.Right - $script:Window.Width - $script:PositionMargin)
  $maxTop = [math]::Max($minTop, [double]$workArea.Bottom - $script:Window.Height - $script:PositionMargin)
  $script:Window.Left = [math]::Max($minLeft, [math]::Min($left, $maxLeft))
  $script:Window.Top = [math]::Max($minTop, [math]::Min($top, $maxTop))
}

function New-Brush([string]$color) { New-Object Windows.Media.SolidColorBrush([Windows.Media.ColorConverter]::ConvertFromString($color)) }

function Load-Image([string]$path) {
  $bitmap = New-Object Windows.Media.Imaging.BitmapImage
  $bitmap.BeginInit()
  $bitmap.UriSource = New-Object Uri($path)
  $bitmap.CacheOption = [Windows.Media.Imaging.BitmapCacheOption]::OnLoad
  $bitmap.EndInit()
  return $bitmap
}

function Format-Money([object]$value, [string]$currency) {
  if ($null -eq $value) { return '--' }
  try { $number = [double]$value } catch { return '--' }
  if ($currency -eq 'CNY' -or $currency -eq 'RMB') { return ('¥ {0:N2}' -f $number) }
  if ($currency -eq 'USD') { return ('$' + ('{0:N4}' -f $number)) }
  return ('{0:N4} {1}' -f $number, $currency)
}

function Set-Text {
  param([string]$Amount, [string]$Hint)
  $script:AmountText.Text = $Amount
  $script:HintText.Text = $Hint
}

function Current-Amount {
  if ($script:LastBalance -ne $null) { return (Format-Money $script:LastBalance $script:LastCurrency) }
  return '--'
}

function Show-Bubble {
  $script:Bubble.Visibility = 'Visible'
  Show-BalanceDisplay
  if ($script:BubbleTimer) { $script:BubbleTimer.Stop() }
  $script:BubbleTimer = New-Object Windows.Threading.DispatcherTimer
  $script:BubbleTimer.Interval = [TimeSpan]::FromSeconds(5)
  $script:BubbleTimer.Add_Tick({ $script:Bubble.Visibility = 'Collapsed'; $script:VoiceBubbleVisible = $false; $script:BubbleTimer.Stop() })
  $script:BubbleTimer.Start()
}

function Play-NextVoice([switch]$KeepBalanceDisplay) {
  if (-not $script:VoiceEnabled -or $script:SoundVolume -le 0) { return }
  $voices = @(Get-SelectedVoices)
  if ($voices.Count -eq 0) { return }
  if ($script:VoiceIndex -ge $voices.Count) { $script:VoiceIndex = 0 }
  $voice = $voices[$script:VoiceIndex]
  $script:VoiceIndex = ($script:VoiceIndex + 1) % $voices.Count
  if (-not $KeepBalanceDisplay) { Show-VoiceBubble $voice }
  Stop-VoicePlayback
  try {
    $player = New-Object Windows.Media.MediaPlayer
    $player.Volume = $script:SoundVolume
    $player.Add_MediaEnded({
      $script:VoicePlaying = $false
      if ($script:VoiceLoop -and $script:VoiceEnabled) { Play-NextVoice }
    })
    $script:VoicePlayer = $player
    $script:VoicePlaying = $true
    $player.Open((New-Object Uri($voice.Path)))
    $player.Play()
  } catch {}
}

function Handle-UserClick {
  if ($script:Bubble.Visibility -ne 'Visible') {
    Show-Bubble
    Start-Refresh
    Play-NextVoice -KeepBalanceDisplay
  } elseif ($script:VoiceBubbleVisible) {
    Show-Bubble
  } else {
    Play-NextVoice
  }
}

function Apply-Scale([double]$value) {
  $next = [math]::Max(0.6, [math]::Min(2.5, $value))
  $oldWidth = $script:Window.Width
  $oldHeight = $script:Window.Height
  $script:Scale = [math]::Round($next, 1)
  $script:Window.Width = $script:BaseSize * $script:Scale
  $script:Window.Height = $script:BaseSize * $script:Scale
  $script:Stage.LayoutTransform = New-Object Windows.Media.ScaleTransform($script:Scale, $script:Scale)
  if (-not $script:RestoringState) {
    if ($script:Window.Left -gt 0) { $script:Window.Left -= ($script:Window.Width - $oldWidth) }
    if ($script:Window.Top -gt 0) { $script:Window.Top -= ($script:Window.Height - $oldHeight) }
  }
  $script:ScaleSlider.Value = $script:Scale
  $script:ScaleLabel.Text = ('{0:N1}x' -f $script:Scale)
  if (-not $script:RestoringState) { Save-State }
}

function Snap-Window {
  $work = [Windows.SystemParameters]::WorkArea
  $cx = $script:Window.Left + ($script:Window.Width / 2)
  $cy = $script:Window.Top + ($script:Window.Height / 2)
  if ($cx -lt ($work.Left + $work.Width / 4)) { $script:Window.Left = $work.Left }
  elseif ($cx -gt ($work.Left + $work.Width * 3 / 4)) { $script:Window.Left = $work.Right - $script:Window.Width }
  else { $script:Window.Left = [math]::Max($work.Left, [math]::Min($script:Window.Left, $work.Right - $script:Window.Width)) }
  if ($cy -lt ($work.Top + $work.Height / 4)) { $script:Window.Top = $work.Top }
  elseif ($cy -gt ($work.Top + $work.Height * 3 / 4)) { $script:Window.Top = $work.Bottom - $script:Window.Height }
  else { $script:Window.Top = [math]::Max($work.Top, [math]::Min($script:Window.Top, $work.Bottom - $script:Window.Height)) }
  Save-State
}

function Start-Refresh {
  if ($script:RefreshJob) { return }
  if (-not $script:VoiceBubbleVisible) {
    $amount = if ($script:LastBalance -ne $null) { Format-Money $script:LastBalance $script:LastCurrency } else { '...' }
    Set-Text $amount '加载中...'
  }
  $script:RefreshJob = Start-Job -ScriptBlock {
    param($url)
    try {
      try {
        $health = Invoke-RestMethod -Uri ($url + '/health') -Method Get -TimeoutSec 5
        if (-not $health.ok) { throw [Exception]::new('本地账户代理健康检查失败') }
      } catch {
        return [pscustomobject]@{ ok = $false; code = 'AGENT_UNAVAILABLE'; error = '无法连接本地账户代理 127.0.0.1:17891' }
      }
      $balance = Invoke-RestMethod -Uri ($url + '/api/balance') -Method Get -TimeoutSec 25
      if (-not $balance.ok) { throw [Exception]::new([string]$balance.error) }
      $usage = $null
      try { $usage = Invoke-RestMethod -Uri ($url + '/api/today-usage') -Method Get -TimeoutSec 25 } catch {}
      [pscustomobject]@{
        ok = $true
        balance = [double]$balance.totalBalance
        currency = [string]$balance.currency
        usage = if ($usage -and $usage.ok) { [double]$usage.amount } else { $null }
        usageCurrency = if ($usage -and $usage.currency) { [string]$usage.currency } else { 'USD' }
        usageError = if ($usage -and -not $usage.ok) { [string]$usage.error } else { '' }
      }
    } catch {
      [pscustomobject]@{ ok = $false; code = 'UPSTREAM'; error = '本地代理已连接，但 Ergouzi 上游请求失败: ' + [string]$_.Exception.Message }
    }
  } -ArgumentList $script:AgentUrl
}

function Complete-Refresh {
  if (-not $script:RefreshJob) { return }
  if ($script:RefreshJob.State -notin @('Completed', 'Failed', 'Stopped')) { return }
  $result = Receive-Job $script:RefreshJob -ErrorAction SilentlyContinue | Select-Object -Last 1
  Remove-Job $script:RefreshJob -Force -ErrorAction SilentlyContinue
  $script:RefreshJob = $null
  if ($result -and $result.ok) {
    $script:LastBalance = $result.balance
    $script:LastCurrency = $result.currency
    $script:LastUsage = $result.usage
    if (-not $script:VoiceBubbleVisible) {
      $usageText = if ($null -eq $script:LastUsage) { '--' } else { Format-Money $script:LastUsage $result.usageCurrency }
      Set-Text (Format-Money $script:LastBalance $script:LastCurrency) ('今日已用 ' + $usageText)
    }
  } else {
    if (-not $script:VoiceBubbleVisible) {
      $prefix = if ($result.code -eq 'AGENT_UNAVAILABLE') { '' } else { '账户代理错误: ' }
      Set-Text (Current-Amount) ($prefix + [string]$result.error)
    }
  }
}

$script:Players = @()
$window = New-Object Windows.Window
$script:Window = $window
$window.WindowStyle = 'None'
$window.WindowStartupLocation = 'Manual'
$window.ResizeMode = 'NoResize'
$window.AllowsTransparency = $true
$window.Background = [Windows.Media.Brushes]::Transparent
$window.ShowInTaskbar = $false
$window.Topmost = -not $NoTopmost
$window.Width = $script:BaseSize
$window.Height = $script:BaseSize

$stage = New-Object Windows.Controls.Canvas
$script:Stage = $stage
$stage.Width = $script:BaseSize
$stage.Height = $script:BaseSize
$stage.Background = [Windows.Media.Brushes]::Transparent
$window.Content = $stage

$bubble = New-Object Windows.Shapes.Ellipse
$script:Bubble = $bubble
$bubble.Width = 250; $bubble.Height = 162; $bubble.Fill = New-Brush $script:Colors.Bubble; $bubble.Stroke = New-Brush $script:Colors.Outline; $bubble.StrokeThickness = 5
[Windows.Controls.Canvas]::SetLeft($bubble, 12); [Windows.Controls.Canvas]::SetTop($bubble, 8); $stage.Children.Add($bubble) | Out-Null

$tail = New-Object Windows.Shapes.Polygon
$tail.Points = '64,154 122,162 83,201'; $tail.Fill = New-Brush $script:Colors.Bubble; $tail.Stroke = New-Brush $script:Colors.Outline; $tail.StrokeThickness = 5; $tail.StrokeLineJoin = 'Round'
$stage.Children.Add($tail) | Out-Null

$dot1 = New-Object Windows.Shapes.Ellipse; $dot1.Width = 22; $dot1.Height = 16; $dot1.Fill = New-Brush $script:Colors.Bubble; $dot1.Stroke = New-Brush $script:Colors.Outline; $dot1.StrokeThickness = 4
[Windows.Controls.Canvas]::SetLeft($dot1, 76); [Windows.Controls.Canvas]::SetTop($dot1, 207); $stage.Children.Add($dot1) | Out-Null
$dot2 = New-Object Windows.Shapes.Ellipse; $dot2.Width = 14; $dot2.Height = 11; $dot2.Fill = New-Brush $script:Colors.Bubble; $dot2.Stroke = New-Brush $script:Colors.Outline; $dot2.StrokeThickness = 3
[Windows.Controls.Canvas]::SetLeft($dot2, 108); [Windows.Controls.Canvas]::SetTop($dot2, 232); $stage.Children.Add($dot2) | Out-Null

$bubblePanel = New-Object Windows.Controls.StackPanel
$bubblePanel.Width = 210; $bubblePanel.HorizontalAlignment = 'Center'
[Windows.Controls.Canvas]::SetLeft($bubblePanel, 32); [Windows.Controls.Canvas]::SetTop($bubblePanel, 37); $stage.Children.Add($bubblePanel) | Out-Null
foreach ($kind in @('Label','Amount','Hint')) {
  $tb = New-Object Windows.Controls.TextBlock; $tb.TextAlignment = 'Center'; $tb.Foreground = New-Brush $script:Colors.Muted; $tb.FontFamily = 'Microsoft YaHei'; $tb.FontWeight = 'SemiBold'; $tb.Margin = '0,0,0,2'
  if ($kind -eq 'Label') { $tb.FontSize = 18; $script:LabelText = $tb; $tb.Text = 'ergouzi余额' }
  elseif ($kind -eq 'Amount') { $tb.FontSize = 32; $tb.FontWeight = 'Bold'; $script:AmountText = $tb; $tb.Text = '...' }
  else { $tb.FontSize = 13; $tb.Foreground = New-Brush $script:Colors.Muted; $script:HintText = $tb; $tb.Text = '加载中...' }
  $bubblePanel.Children.Add($tb) | Out-Null
}

$imagePath = Join-Path $script:RootDir 'assets\ergouzi-character-cutout.png'
$image = New-Object Windows.Controls.Image; $image.Source = Load-Image $imagePath; $image.Width = 178; $image.Height = 178; $image.Stretch = 'Uniform'; $image.Cursor = 'Hand'
[Windows.Controls.Canvas]::SetLeft($image, 137); [Windows.Controls.Canvas]::SetTop($image, 142); $stage.Children.Add($image) | Out-Null

Initialize-VoiceCatalog
$menu = New-Object Windows.Controls.ContextMenu
$menu.Background = New-Brush $script:Colors.Menu
$menu.Foreground = New-Brush $script:Colors.Text
$sizeItem = New-Object Windows.Controls.MenuItem; $sizeItem.Header = '大小'
$sizePanel = New-Object Windows.Controls.StackPanel; $sizePanel.Orientation = 'Horizontal'
$slider = New-Object Windows.Controls.Slider; $script:ScaleSlider = $slider; $slider.Minimum = 0.6; $slider.Maximum = 2.5; $slider.Value = 1; $slider.Width = 130; $slider.TickFrequency = 0.1; $slider.IsSnapToTickEnabled = $true
$sizeLabel = New-Object Windows.Controls.TextBlock; $script:ScaleLabel = $sizeLabel; $sizeLabel.Margin = '8,0,0,0'; $sizeLabel.Width = 40; $sizeLabel.Text = '1.0x'
$slider.Add_ValueChanged({ Apply-Scale $script:ScaleSlider.Value }); $sizePanel.Children.Add($slider) | Out-Null; $sizePanel.Children.Add($sizeLabel) | Out-Null; $sizeItem.Items.Add($sizePanel) | Out-Null; $menu.Items.Add($sizeItem) | Out-Null
$volumeItem = New-Object Windows.Controls.MenuItem; $volumeItem.Header = '语音音量'
$volumePanel = New-Object Windows.Controls.StackPanel; $volumePanel.Orientation = 'Horizontal'
$volumeSlider = New-Object Windows.Controls.Slider; $volumeSlider.Minimum = 0; $volumeSlider.Maximum = 1; $volumeSlider.Width = 130; $volumeSlider.TickFrequency = 0.1; $volumeSlider.IsSnapToTickEnabled = $true; $volumeSlider.Value = $script:SoundVolume
$volumeLabel = New-Object Windows.Controls.TextBlock; $volumeLabel.Margin = '8,0,0,0'; $volumeLabel.Width = 40; $volumeLabel.Text = ('{0:P0}' -f $script:SoundVolume)
$volumeSlider.Add_ValueChanged({ $script:SoundVolume = [double]$volumeSlider.Value; $volumeLabel.Text = ('{0:P0}' -f $script:SoundVolume); if ($script:VoicePlayer) { $script:VoicePlayer.Volume = $script:SoundVolume }; Save-State })
$volumePanel.Children.Add($volumeSlider) | Out-Null; $volumePanel.Children.Add($volumeLabel) | Out-Null; $volumeItem.Items.Add($volumePanel) | Out-Null; $menu.Items.Add($volumeItem) | Out-Null
$voiceMenu = New-Object Windows.Controls.MenuItem; $voiceMenu.Header = '角色语音（中文/日文）'
$voiceEnabledItem = New-Object Windows.Controls.MenuItem; $script:VoiceEnabledItem = $voiceEnabledItem; $voiceEnabledItem.Header = '点击播放语音'; $voiceEnabledItem.IsCheckable = $true; $voiceEnabledItem.IsChecked = $true
$voiceEnabledItem.Add_Click({ $script:VoiceEnabled = [bool]$voiceEnabledItem.IsChecked; if (-not $script:VoiceEnabled) { Stop-VoicePlayback }; Save-State })
$voiceMenu.Items.Add($voiceEnabledItem) | Out-Null
$voiceLoopItem = New-Object Windows.Controls.MenuItem; $script:VoiceLoopItem = $voiceLoopItem; $voiceLoopItem.Header = '循环播放已选语音'; $voiceLoopItem.IsCheckable = $true; $voiceLoopItem.IsChecked = $false
$voiceLoopItem.Add_Click({ $script:VoiceLoop = [bool]$voiceLoopItem.IsChecked; Save-State; if ($script:VoiceLoop -and $script:VoiceEnabled -and -not $script:VoicePlayer) { Play-NextVoice } })
$voiceMenu.Items.Add($voiceLoopItem) | Out-Null
$voiceMenu.Items.Add((New-Object Windows.Controls.Separator)) | Out-Null
$allCn = New-Object Windows.Controls.MenuItem; $allCn.Header = '全选中文'; $allCn.Add_Click({ Set-VoiceSelectionGroup 'cn' $true }); $voiceMenu.Items.Add($allCn) | Out-Null
$allJp = New-Object Windows.Controls.MenuItem; $allJp.Header = '全选日文'; $allJp.Add_Click({ Set-VoiceSelectionGroup 'jp' $true }); $voiceMenu.Items.Add($allJp) | Out-Null
$allVoice = New-Object Windows.Controls.MenuItem; $allVoice.Header = '全选全部语音'; $allVoice.Add_Click({ foreach ($voice in $script:VoiceCatalog) { Set-VoiceSelection $voice.Key $true } }); $voiceMenu.Items.Add($allVoice) | Out-Null
$noneVoice = New-Object Windows.Controls.MenuItem; $noneVoice.Header = '清空选择'; $noneVoice.Add_Click({ foreach ($voice in $script:VoiceCatalog) { Set-VoiceSelection $voice.Key $false } }); $voiceMenu.Items.Add($noneVoice) | Out-Null
$voiceMenu.Items.Add((New-Object Windows.Controls.Separator)) | Out-Null
foreach ($voice in $script:VoiceCatalog) {
  $voiceItem = New-Object Windows.Controls.MenuItem
  $voiceItem.Tag = $voice.Key
  $voiceItem.Header = "[$($voice.Language)] $($voice.Name)"
  $voiceItem.Foreground = New-Brush $voice.Color
  $voiceItem.IsCheckable = $true
  $voiceItem.IsChecked = $true
  $voiceItem.ToolTip = "$($voice.Language)：$($voice.Name)"
  $script:VoiceMenuItems[$voice.Key] = $voiceItem
  $voiceItem.Add_Click({ $clicked = $_.Source; Set-VoiceSelection ([string]$clicked.Tag) ([bool]$clicked.IsChecked) })
  $voiceMenu.Items.Add($voiceItem) | Out-Null
}
$menu.Items.Add($voiceMenu) | Out-Null
$refreshItem = New-Object Windows.Controls.MenuItem; $refreshItem.Header = '立即刷新'; $refreshItem.Add_Click({ Start-Refresh; Show-Bubble }); $menu.Items.Add($refreshItem) | Out-Null
$exitItem = New-Object Windows.Controls.MenuItem; $exitItem.Header = '退出挂件'; $exitItem.Add_Click({ $window.Close() }); $menu.Items.Add($exitItem) | Out-Null

$menuButton = New-Object Windows.Controls.Button; $menuButton.Content = '⋮'; $menuButton.FontSize = 18; $menuButton.Width = 28; $menuButton.Height = 28; $menuButton.Padding = 0; $menuButton.Background = New-Brush $script:Colors.Outline; $menuButton.Foreground = [Windows.Media.Brushes]::White; $menuButton.BorderThickness = 0; $menuButton.ContextMenu = $menu
[void]$menuButton.Add_Click({ $menu.IsOpen = $true })
[void]$menuButton.Add_MouseLeftButtonDown({ $_.Handled = $true })
[Windows.Controls.Canvas]::SetLeft($menuButton, 238); [Windows.Controls.Canvas]::SetTop($menuButton, 32); $stage.Children.Add($menuButton) | Out-Null

$script:Bubble.Visibility = 'Collapsed'
$stage.Add_MouseLeftButtonDown({
  $script:Drag = [pscustomobject]@{ x = [Windows.Input.Mouse]::GetPosition($null).X; y = [Windows.Input.Mouse]::GetPosition($null).Y; left = $window.Left; top = $window.Top; moved = $false }
  $stage.CaptureMouse()
})
$stage.Add_MouseMove({
  if (-not $script:Drag -or [Windows.Input.Mouse]::LeftButton -ne 'Pressed') { return }
  $p = [Windows.Input.Mouse]::GetPosition($null); $dx = $p.X - $script:Drag.x; $dy = $p.Y - $script:Drag.y
  if (($dx * $dx + $dy * $dy) -ge 9) { $script:Drag.moved = $true }
  $window.Left = $script:Drag.left + $dx; $window.Top = $script:Drag.top + $dy
})
$stage.Add_MouseLeftButtonUp({
  if (-not $script:Drag) { return }
  $drag = $script:Drag; $script:Drag = $null; $stage.ReleaseMouseCapture()
  if (-not $drag.moved) { Handle-UserClick }
  else { Snap-Window }
})
$bubble.Add_MouseLeftButtonDown({ $_.Handled = $true; if ($script:VoiceBubbleVisible) { Show-Bubble } else { Play-NextVoice } })

$state = Read-State
if ($state) {
  if ($state.scale) { $script:Scale = [double]$state.scale }
  if ($state.volume -ne $null) { $script:SoundVolume = [double]$state.volume }
  if ($state.voiceEnabled -ne $null) { $script:VoiceEnabled = [bool]$state.voiceEnabled; $voiceEnabledItem.IsChecked = $script:VoiceEnabled }
  elseif ($state.sound -ne $null) { $script:VoiceEnabled = [bool]$state.sound; $voiceEnabledItem.IsChecked = $script:VoiceEnabled }
  if ($state.voiceLoop -ne $null) { $script:VoiceLoop = [bool]$state.voiceLoop; $voiceLoopItem.IsChecked = $script:VoiceLoop }
  if ($state.selectedVoices -ne $null) {
    $savedKeys = @($state.selectedVoices | ForEach-Object { [string]$_ })
    foreach ($voice in $script:VoiceCatalog) {
      if ($script:VoiceMenuItems.ContainsKey($voice.Key)) { $script:VoiceMenuItems[$voice.Key].IsChecked = $savedKeys -contains $voice.Key }
    }
    $script:SelectedVoiceKeys = @($savedKeys | Where-Object { $script:VoiceCatalog.Key -contains $_ })
  }
}
$volumeSlider.Value = $script:SoundVolume
$volumeLabel.Text = ('{0:P0}' -f $script:SoundVolume)
$workArea = [Windows.SystemParameters]::WorkArea
$script:RestoringState = $true
Apply-Scale $script:Scale
$script:RestoringState = $false
Set-WindowPosition $state $workArea
$window.Add_ContentRendered({
  # Reapply after WPF/DPI layout so a stale or cross-monitor position cannot hide the window.
  Set-WindowPosition $state ([Windows.SystemParameters]::WorkArea)
  Save-State
})
$window.Add_Closed({
  if ($script:RefreshJob) { Stop-Job $script:RefreshJob -ErrorAction SilentlyContinue; Remove-Job $script:RefreshJob -Force -ErrorAction SilentlyContinue }
  Stop-VoicePlayback
  foreach ($player in $script:Players) { try { $player.Close() } catch {} }
  Save-State
  $mutex.ReleaseMutex(); $mutex.Dispose()
})

$poll = New-Object Windows.Threading.DispatcherTimer; $poll.Interval = [TimeSpan]::FromMilliseconds(250); $poll.Add_Tick({ Complete-Refresh }); $poll.Start()
$refresh = New-Object Windows.Threading.DispatcherTimer; $refresh.Interval = [TimeSpan]::FromMinutes(1); $refresh.Add_Tick({ Start-Refresh }); $refresh.Start()
$window.Show()
Show-Bubble
Start-Refresh
[Windows.Threading.Dispatcher]::Run()
