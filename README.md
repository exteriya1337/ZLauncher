# ZLauncher

Minecraft-лаунчер (Avalonia) + установщик + сайт.

**Не связан с Mojang Studios / Microsoft.** Minecraft — товарный знак Mojang Synergies AB.

## Репозиторий

| Путь | Содержимое |
|------|------------|
| `/` | Лаунчер `ZLauncher` |
| `Installer/` | Установщик `ZLauncher.Setup` |
| `site/` | Лендинг (GitHub Pages) |
| `.github/workflows/release.yml` | Сборка portable + setup в Release |

- **Owner / repo:** `exteriya1337/ZLauncher`
- **Релизы:** https://github.com/exteriya1337/ZLauncher/releases
- **Сайт (Pages):** включи в Settings → Pages → branch `main` / folder `/site`

## Версия

Единая версия: `Directory.Build.props` + `Services/AppInfo.cs` (`Version = "1.0.0"`).

При релизе:

1. Обнови `Version` в `AppInfo.cs` (лаунчер) и `Installer/Services/AppInfo.cs`
2. Обнови `Directory.Build.props`
3. Тег: `git tag v1.0.1 && git push origin v1.0.1`
4. CI соберёт `ZLauncher-Portable.zip` и `ZLauncher.Setup.exe`

## Локальная сборка

```powershell
# Лаунчер
dotnet build ZLauncher.csproj -c Release

# Portable
dotnet publish ZLauncher.csproj -c Release -r win-x64 --self-contained true -o .\publish\portable

# Payload + Setup
$zip = ".\Installer\payload\payload.zip"
Compress-Archive -Path .\publish\portable\* -DestinationPath $zip -Force
dotnet publish .\Installer\ZLauncher.Installer.csproj -c Release -r win-x64 --self-contained true `
  /p:PublishSingleFile=true -o .\publish\setup
```

Или: `Installer\tools\Publish-Setup.ps1` (если рядом есть `Desktop\ZLauncher-Portable`).

## Обновления

При старте лаунчер дергает GitHub API `releases/latest` и сравнивает с `AppInfo.Version`.  
Если есть новее — статус «Доступно обновление…»; команда `ApplyLauncherUpdate` качает `ZLauncher.Setup.exe` и запускает.

## Кастомные версии

Положи папку с profile `.json` (+ jar) в:

`%AppData%\ZLauncher\versions\MyClient\`

Она появится в списке версий (тип **custom**), сверху. Debug-консоль — **3× клик** по «ZLauncher» в titlebar (без подсказки).

## Лицензия / дисклеймер

Неофициальный клиент. Не аффилирован с Mojang / Microsoft.
