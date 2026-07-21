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

## Установщик и обновления

**`ZLauncher.Setup.exe` — онлайн-stub:** при установке всегда качает последний `ZLauncher-Portable.zip` с GitHub (`releases/latest`).  
Один Setup можно хранить долго — он ставит актуальную версию (нужен интернет).

**Принудительное обновление лаунчера:** при каждом запуске (splash) сверка с GitHub.  
Если на GitHub версия новее — скачивается Portable, файлы заменяются, лаунчер перезапускается.  
Без сети — запуск продолжается без обновления.

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

# Онлайн Setup-stub (без вшитого payload)
.\Installer\tools\Publish-Setup.ps1
```

Релиз: portable + online Setup в assets. Тег `v*`, CI или ручная заливка.

## Лицензия / дисклеймер

Неофициальный проект. Не аффилирован с Mojang, Microsoft или Minecraft.
