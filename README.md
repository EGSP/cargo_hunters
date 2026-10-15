# Cargo Hunters: мод инвентаря

Рабочая папка для исследования игры и разработки сортировки инвентаря через BepInEx.

## Проверенное окружение

- Игра: `C:\workroot\software\steam\steamapps\common\Cargo Hunters`.
- Версия игры: `0.26.39.61`, Steam build `25409884`.
- Unity: `2021.3.45f2`, Windows x64, IL2CPP.
- Присутствуют `GameAssembly.dll` и `CargoHunters_Data\il2cpp_data\Metadata\global-metadata.dat`.
- Установлен BepInEx `6.0.0-be.788+5b766a3`, вариант `Unity.IL2CPP-win-x64`.
- Проверена загрузка BepInEx и C# плагина `0.5.0` с локальным HTTP-мостом, событиями клавиатуры и JSON-выгрузкой инвентарей (15 октября 2026 года). Код сортировки ещё не написан.

## Папки

- `src/` — исходный код будущего мода.
- `docs/` — результаты исследования игровых типов и операций инвентаря.
- `tools/bepinex-6.0.0-be.788/` — исходный архив загрузчика и распакованная копия.
- `backups/saves/` — отдельные снимки сохранений с датой и временем, файлами и SHA-256 в `manifest.json`.

## Первый запуск

Запустить игру обычным способом через Steam. При первом запуске BepInEx для IL2CPP генерирует вспомогательные сборки, поэтому запуск может занять больше времени.

Проверить `BepInEx\LogOutput.log` в папке игры. В установленной сборке используется расширение `.log`. Успешная загрузка подтверждается строками `CARGO_INVENTORY_MOD_READY` и `Chainloader startup complete`. Сгенерированные сборки находятся в `BepInEx\interop`.

Чтобы отключить загрузчик, закрыть игру и в `doorstop_config.ini` поменять `enabled = true` на `enabled = false` в секции `[General]`.

## Сохранения

Фактические сохранения найдены в `C:\workroot\software\steam\userdata\71158143\4197990\remote`.

Начальный снимок: `backups/saves/2026-10-15_14-53-05/`; содержит `offline.save` и `offline.save.bak`. SHA-256 копий совпали с оригиналами при создании. Перед новыми экспериментами создавать новый снимок при закрытой игре.

Для восстановления закрыть игру, сохранить отдельно текущее состояние и скопировать нужный файл из снимка в исходную папку из `manifest.json`. Учитывать синхронизацию Steam Cloud при следующем запуске.

## Следующий этап

Изучить сгенерированные сборки, найти операции перемещения предметов и обновления интерфейса. Первый прототип: сортировка одного контейнера в убежище по команде. Имена игровых классов и методов пока не установлены.

## Плагин и локальный инструмент

Исходники: `src/CargoHunters.InventoryMod/`. Плагин предоставляет HTTP-команды чтения сцен, объектов и компонентов, а также явные вызовы методов. Автосортировки в нём пока нет. [Команды и примеры](docs/dev-bridge.md).

```powershell
.\tools\Build-Deploy.ps1
```

Скрипт собирает `bin\Release\net6.0\CargoHunters.InventoryMod.dll` и устанавливает его в папку игры `BepInEx\plugins\CargoHunters.InventoryMod\`. При запущенной игре установка останавливается. После установки запустить игру через Steam.

```powershell
.\tools\Invoke-GameCommand.ps1 -Route '/scenes'
.\tools\Get-GameUi.ps1
.\tools\Test-GameBridge.ps1
.\tools\Test-GameUi.ps1
```

Результаты первоначальной проверки и ограничения: [docs/verification.md](docs/verification.md).

Результаты исследования открытого инвентаря по дереву сцены: [docs/inventory-ui.md](docs/inventory-ui.md).

## Источники

- https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html
- https://builds.bepinex.dev/projects/bepinex_be
- Архив: https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip
