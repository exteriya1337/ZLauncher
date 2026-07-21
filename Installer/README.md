# ZLauncher.Installer

Мастер установки **ZLauncher** (Avalonia UI, тёмная тема).

## Структура

```
ZLauncher.Installer/
├── Services/          # установка, ярлыки, payload
├── ViewModels/        # мастер (Welcome → Options → Install → Done)
├── Views/             # окно установщика
├── payload/           # файлы лаунчера (ZLauncher.exe + dll)
└── tools/
    ├── Pack-Payload.ps1    # скопировать portable → payload/
    └── Publish-Setup.ps1   # pack + publish на рабочий стол
```

## Быстрый старт

1. Убедись, что есть portable-сборка: `Desktop\ZLauncher-Portable\ZLauncher.exe`
2. Упакуй payload и собери setup:

```powershell
cd $env:USERPROFILE\Desktop\ZLauncher.Installer
.\tools\Publish-Setup.ps1
```

На рабочем столе появится папка **`ZLauncher-Setup`** с `ZLauncher.Setup.exe`.

## Dev-запуск

```powershell
# положить файлы лаунчера
.\tools\Pack-Payload.ps1

dotnet run --project .
```

Без `payload/` установщик сам ищет `Desktop\ZLauncher-Portable`.

## Что делает установщик

| Шаг | Действие |
|-----|----------|
| Копирование | Все файлы из `payload/` → папка установки |
| Ярлыки | Рабочий стол + меню Пуск (по желанию) |
| Uninstall | `Uninstall.bat` + запись в «Программы и компоненты» (HKCU) |
| По умолчанию | `%LocalAppData%\Programs\ZLauncher` |

Данные игры (`%AppData%\ZLauncher`) установщик **не трогает**.

## Дальше (можно развивать)

- Встраивание payload в один exe
- Проверка обновлений / скачивание с сервера
- Выбор языка
- Цифровая подпись
