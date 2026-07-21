# ZLauncher

Неофициальный **лаунчер Minecraft** для Windows: версии, моды (Modrinth), ресурспаки, шейдеры, быстрый запуск.

> **Не связан с Mojang Studios и Microsoft.**  
> Minecraft — товарный знак Mojang Synergies AB.

## Скачать

| Файл | Для чего |
|------|----------|
| [**ZLauncher.Setup.exe**](https://github.com/exteriya1337/ZLauncher/releases/latest/download/ZLauncher.Setup.exe) | Установщик (рекомендуется) |
| [**ZLauncher-Portable.zip**](https://github.com/exteriya1337/ZLauncher/releases/latest/download/ZLauncher-Portable.zip) | Portable, без установки |

Все версии: [Releases](https://github.com/exteriya1337/ZLauncher/releases)  
Сайт: https://exteriya1337.github.io/ZLauncher/

## Что умеет

- Список версий Mojang (релизы, снапшоты, beta/alpha)
- Vanilla / Fabric / Quilt / Forge / NeoForge / OptiFine
- **Кастомные версии** — положи папку в `%AppData%\ZLauncher\versions\`
- Моды, ресурспаки и шейдеры с Modrinth
- Обычные аккаунты (офлайн-ник)
- Проверка обновлений лаунчера через GitHub Releases

## Установщик: важно

**Сейчас Setup — «полный» установщик:** внутри него уже лежит лаунчер той версии, с которой Setup собран.

| Вопрос | Ответ |
|--------|--------|
| Можно один раз скачать Setup и всегда ставить «последнее»? | **Нет.** Старый Setup ставит ту версию, которая была в нём на момент сборки. |
| Как поставить новую версию? | Скачать новый Setup (или Portable) с [latest release](https://github.com/exteriya1337/ZLauncher/releases/latest), либо обновиться **из уже установленного лаунчера** (проверка GitHub при запуске). |
| Лаунчер сам обновляется? | Да: при старте смотрит `releases/latest` и может скачать новый Setup. |

Если понадобится **вечный Setup-stub** (маленький файл, который всегда качает latest с GitHub) — это отдельная доработка.

## Структура репозитория

```
ZLauncher/          — исходники лаунчера (Avalonia)
Installer/          — исходники установщика
docs/               — лендинг (GitHub Pages)
.github/workflows/  — сборка релиза по тегу v*
```

## Сборка (для разработчиков)

```powershell
# Лаунчер (portable)
dotnet publish ZLauncher.csproj -c Release -r win-x64 --self-contained true -o .\publish\portable

# Установщик (упаковать portable внутрь + single-file Setup)
.\Installer\tools\Publish-Setup.ps1
```

Релиз: обновить `Version` в `Services/AppInfo.cs` и `Directory.Build.props`, затем:

```powershell
git tag v1.0.2
git push origin v1.0.2
```

CI соберёт `ZLauncher.Setup.exe` и `ZLauncher-Portable.zip`.

## Лицензия / дисклеймер

Неофициальный проект. Не аффилирован с Mojang, Microsoft или Minecraft.
