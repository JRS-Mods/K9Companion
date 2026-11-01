param(
    [string] $SPTPath = 'F:\SPT',
    [switch] $NoInstall
)

$ErrorActionPreference = 'Stop'
$name = 'K9Companion'
$root = $PSScriptRoot
$project = Join-Path $root "$name.csproj"

if (-not (Test-Path (Join-Path $SPTPath 'BepInEx\core\BepInEx.dll'))) {
    throw "$SPTPath doesn't look like an SPT folder (no BepInEx\core\BepInEx.dll)."
}

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$pluginVersion = (Select-String -Path (Join-Path $root 'Plugin.cs') -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
if ($version -ne $pluginVersion) { throw "csproj says $version but Plugin.cs says $pluginVersion." }

$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = if (Test-Path $userDotnet) { $userDotnet } else { 'dotnet' }

& $dotnet build $project -c Release "-p:SPTPath=$SPTPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# The Fika link: built against Fika (active or parked), never ships Fika itself, and is only loaded where Fika is installed.
$fikaLink = $null
$linkProject = Join-Path (Split-Path $root -Parent) 'K9Companion.Fika\K9Companion.Fika.csproj'
if ((Test-Path (Join-Path $SPTPath 'BepInEx\plugins\Fika\Fika.Core.dll')) -or (Test-Path (Join-Path $SPTPath '_mods_disabled\BepInEx\plugins\Fika\Fika.Core.dll'))) {
    & $dotnet build $linkProject -c Release "-p:SPTPath=$SPTPath" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Fika link build failed.' }
    $fikaLink = Join-Path (Split-Path $linkProject -Parent) 'bin\Release\K9Companion.Fika.dll'
}
else { Write-Warning 'Fika.Core.dll not found: packing without the Fika link.' }

# The server half (jackets, Dr. Yang, the K9 questline and kennel, and their bookkeeping) lives next to this folder.
$serverName = 'K9Companion.Server'
$serverProject = Join-Path (Split-Path $root -Parent) "$serverName\$serverName.csproj"
& $dotnet build $serverProject -c Release "-p:SPTPath=$SPTPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Server mod build failed.' }
$serverDll = Join-Path (Split-Path $serverProject -Parent) "bin\Release\$serverName.dll"
$serverPortrait = Join-Path (Split-Path $serverProject -Parent) 'res\dryang.jpg'
if (-not (Test-Path $serverPortrait)) { throw "Missing $serverPortrait (tools\render_portrait.py makes it)." }
# The K9 quest pictures (tools\quest_images.py scales them from E:\SPT Mod\Assets); quests without one show the portrait.
$questPictureFolder = Join-Path (Split-Path $serverProject -Parent) 'res\quests'
$questPictures = @()
if (Test-Path $questPictureFolder) { $questPictures = @(Get-ChildItem -LiteralPath $questPictureFolder -Filter '*.jpg' -File) }

$files = @(
    (Join-Path $root "bin\Release\$name.dll"),
    (Join-Path $root 'assets\k9dog.dog'),
    (Join-Path $root 'assets\k9dog.png'),
    (Join-Path $root 'assets\kennel.props'),
    (Join-Path $root 'assets\kennel_bed.jpg'),
    (Join-Path $root 'assets\kennel_bowl.jpg'),
    (Join-Path $root 'assets\dog_outline.png'),
    (Join-Path $root 'assets\kennel_icon.png'),
    (Join-Path $root 'README.md'),
    (Join-Path $root 'LICENSE.txt')
)
if ($fikaLink) { $files += $fikaLink }
# The dog's gear (tools/convert_gear.py): each harness and collar model with its texture, and each harness's stash
# icon (tools/render_gear_icons.py).
foreach ($look in 'harness_chestrig', 'harness_pocket', 'harness_tactical', 'harness_assault', 'harness_camera') {
    $files += (Join-Path $root "assets\gear_$look.mesh"), (Join-Path $root "assets\gear_$look.jpg"), (Join-Path $root "assets\icon_$look.png")
}
# Head gear (tools/convert_gear.py, kind head) and the clip-on camera (tools/convert_attachment.py: one model per harness
# without its own camera, one texture).
foreach ($look in 'head_goggles_tan', 'head_goggles_black') {
    $files += (Join-Path $root "assets\gear_$look.mesh"), (Join-Path $root "assets\gear_$look.jpg"), (Join-Path $root "assets\icon_$look.png")
}
# The camera headset was taken out in 0.29.1 (any owned become action cameras); its stash picture stays for the moment
# before that happens.
$files += (Join-Path $root 'assets\icon_head_headset.png')
foreach ($look in 'harness_chestrig', 'harness_pocket', 'harness_tactical', 'harness_assault') { $files += (Join-Path $root "assets\gear_camera_action_$look.mesh") }
$files += (Join-Path $root 'assets\gear_camera_action.jpg'), (Join-Path $root 'assets\icon_camera_action.png')
# Each piece of gear off the dog, for the inspect window and the ground (tools/convert_item_models.py).
foreach ($look in 'harness_chestrig', 'harness_pocket', 'harness_tactical', 'harness_assault', 'harness_camera', 'head_goggles_tan', 'head_goggles_black', 'camera_action') {
    $files += (Join-Path $root "assets\gear_${look}_item.mesh")
}
# The custom kennel models (tools/convert_kennel_models.py).
$files += (Join-Path $root 'assets\kennel_models.props')
# Each with its colour, normal map and metal/smoothness map.
foreach ($prop in 'crate', 'doghouse', 'toybox', 'foodbag') {
    $files += (Join-Path $root "assets\kennel_$prop.jpg"), (Join-Path $root "assets\kennel_${prop}_n.png"), (Join-Path $root "assets\kennel_${prop}_ms.png")
}
foreach ($style in 'training', 'black', 'buckle') {
    $files += (Join-Path $root "assets\gear_collar_$style.mesh"), (Join-Path $root "assets\gear_collar_$style.jpg")
    # The collar as an item (0.31): off the dog (tools/convert_item_models.py) and its stash icon (tools/render_gear_icons.py).
    $files += (Join-Path $root "assets\gear_collar_${style}_item.mesh"), (Join-Path $root "assets\icon_collar_$style.png")
}
# The dog's supplies and K9 story items as their own models (tools/convert_dog_items.py): model, its three maps, icon.
foreach ($key in 'can', 'meat', 'duffel', 'tag', 'vetkit') {
    foreach ($suffix in '.mesh', '.jpg', '_n.png', '_ms.png') { $files += (Join-Path $root "assets\gear_item_$key$suffix") }
    $files += (Join-Path $root "assets\icon_item_$key.png")
}
# The vet supply case (1x1, no longer spawns; players may still have one) shares the vet kit's model: its own icon at its size.
$files += (Join-Path $root 'assets\icon_item_vetkit_1x1.png')
$soundFolder = Join-Path $root 'assets\sounds'
$soundFiles = @()
if (Test-Path $soundFolder) { $soundFiles = @(Get-ChildItem -LiteralPath $soundFolder -Filter '*.wav' -File) }
foreach ($f in $files) { if (-not (Test-Path $f)) { throw "Missing $f" } }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "$name-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($f in $files) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f, "BepInEx/plugins/$name/" + (Split-Path $f -Leaf)) | Out-Null
    }
    foreach ($sound in $soundFiles) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $sound.FullName, "BepInEx/plugins/$name/sounds/" + $sound.Name) | Out-Null
    }
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $serverDll, "SPT_Runtime/user/mods/$serverName/$serverName.dll") | Out-Null
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $serverPortrait, "SPT_Runtime/user/mods/$serverName/res/dryang.jpg") | Out-Null
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $root 'LICENSE.txt'), "SPT_Runtime/user/mods/$serverName/LICENSE.txt") | Out-Null
    foreach ($picture in $questPictures) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $picture.FullName, "SPT_Runtime/user/mods/$serverName/res/quests/" + $picture.Name) | Out-Null
    }
}
finally {
    $archive.Dispose()
}
Write-Host "Packed $zip"

if (-not $NoInstall) {
    $dest = Join-Path $SPTPath "BepInEx\plugins\$name"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $files $dest -Force
    # 0.18.4-0.19.2 brought their own QBZ-191 and Type 20; the WTT Content Backport has those now, under the same ids and
    # bundle names, so what's left of them is taken out (their bundles would be served alongside WTT's).
    $serverLeftovers = Join-Path $SPTPath "SPT_Runtime\user\mods\$serverName"
    foreach ($old in (Join-Path $dest 'live_gun_anims.json'), (Join-Path $serverLeftovers 'bundles.json'),
                     (Join-Path $serverLeftovers 'bundles'), (Join-Path $serverLeftovers 'res\live_guns.json')) {
        if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force; Write-Host "Removed old $old" }
    }
    # Before 0.20 the dog wore one jacket model in six colours; the gear models replaced it.
    foreach ($old in @('jacket.mesh') + @('black', 'olive', 'coyote', 'blue', 'hivis', 'elite' | ForEach-Object { "jacket_$_.jpg"; "icon_$_.png" })) {
        $path = Join-Path $dest $old
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    # 0.24.5 named the dog's gear files by slot kind (harness_, head_, camera_action); the old names are taken out.
    foreach ($look in 'chestrig', 'vest_pocket', 'vest', 'vest_large', 'vest_k9cam', 'goggles_tan', 'goggles_black', 'headset_cam', 'camera') {
        foreach ($old in "gear_$look.mesh", "gear_$look.jpg", "icon_$look.png") {
            $path = Join-Path $dest $old
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
    }
    # 0.29.1 took the camera headset out: its models go.
    foreach ($old in 'gear_head_headset.mesh', 'gear_head_headset.jpg', 'gear_head_headset_item.mesh') {
        $path = Join-Path $dest $old
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    Get-ChildItem -LiteralPath $dest -Filter 'gear_camera_*.mesh' -File | Where-Object { $_.Name -notlike 'gear_camera_action_*' } | Remove-Item
    if ($soundFiles.Count -gt 0) {
        $soundDest = Join-Path $dest 'sounds'
        New-Item -ItemType Directory -Force $soundDest | Out-Null
        # The sounds folder is the mod's: one no longer shipped (0.21's pant.wav became breathing.wav) is taken out.
        Get-ChildItem -LiteralPath $soundDest -Filter '*.wav' -File | Where-Object { $soundFiles.Name -notcontains $_.Name } | Remove-Item
        Copy-Item -LiteralPath $soundFiles.FullName -Destination $soundDest -Force
    }
    Write-Host "Installed to $dest"
    $serverDest = Join-Path $SPTPath "SPT_Runtime\user\mods\$serverName"
    New-Item -ItemType Directory -Force $serverDest | Out-Null
    # A running server locks its DLL: move that one aside (it keeps running) and clear older ones once they're free.
    Get-ChildItem $serverDest -Filter '*.dll.previous*' -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -ErrorAction SilentlyContinue }
    $installed = Join-Path $serverDest "$serverName.dll"
    if (Test-Path $installed) {
        try { [IO.File]::Open($installed, 'Open', 'ReadWrite', 'None').Close() }
        catch { Rename-Item $installed "$serverName.dll.previous-$(Get-Date -Format yyyyMMddHHmmss)" }
    }
    Copy-Item $serverDll $serverDest -Force
    New-Item -ItemType Directory -Force (Join-Path $serverDest 'res') | Out-Null
    Copy-Item $serverPortrait (Join-Path $serverDest 'res') -Force
    if ($questPictures.Count -gt 0) {
        New-Item -ItemType Directory -Force (Join-Path $serverDest 'res\quests') | Out-Null
        Copy-Item -LiteralPath $questPictures.FullName -Destination (Join-Path $serverDest 'res\quests') -Force
    }
    Write-Host "Installed server mod to $serverDest (restart the SPT server)"
}
