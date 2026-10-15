# Локальные команды работающей игры

Инструмент входит в плагин `Cargo Hunters Inventory Mod 0.5.0`. Он работает внутри процесса игры и не зависит от управления окном. Unity-команды выполняются в `MonoBehaviour.Update`, а HTTP обслуживается отдельными задачами. Плагин включает `Application.runInBackground`, чтобы команды обрабатывались при потере фокуса окна; при уничтожении компонента восстанавливает прежнее значение.

## Использование из PowerShell

Запросы можно отправлять напрямую из терминала. PowerShell-файл клиента не обязателен и не меняется между запросами:

```powershell
$connection = Get-Content 'C:\workroot\software\steam\steamapps\common\Cargo Hunters\BepInEx\config\cargo-dev-bridge.json' -Raw | ConvertFrom-Json
curl.exe -sS -H "Authorization: Bearer $($connection.token)" "$($connection.url)/scenes"

# Передача JSON через stdin сохраняет кавычки в Windows PowerShell 5.1.
'{"key":"Tab","frames":3}' | curl.exe -sS -H "Authorization: Bearer $($connection.token)" -H 'Content-Type: application/json' --data-binary '@-' "$($connection.url)/input/key"
```

Тот же HTTP-интерфейс доступен из Python `urllib.request`, любого другого HTTP-клиента или терминала. После перезапуска игры перечитать connection-файл: токен меняется.

Из рабочей папки проекта:

```powershell
# Доступность сервера и счётчик кадров главного потока.
.\tools\Invoke-GameCommand.ps1 -Route '/health'

# Текущая и все загруженные сцены.
.\tools\Invoke-GameCommand.ps1 -Route '/scenes'

# Корневые объекты, включая DontDestroyOnLoad.
.\tools\Invoke-GameCommand.ps1 -Route '/objects?depth=0&limit=60'

# Поиск по части имени; depth задаёт глубину от корней.
.\tools\Invoke-GameCommand.ps1 -Route '/objects?name=Inventory&depth=20&limit=30'
```

Полученный `id` подставить в следующие команды (переменные `$objectId` и `$componentId` — ID из ответов текущего запуска):

```powershell
# Компоненты и непосредственные дочерние объекты.
.\tools\Invoke-GameCommand.ps1 -Route "/object?id=$objectId"

# Поиск внутри одного выбранного объекта: полезен, если общий поиск усечён.
.\tools\Invoke-GameCommand.ps1 -Route "/objects?rootId=$objectId&name=Inventory&depth=20"

# Поля, список свойств и сигнатуры публичных методов компонента.
.\tools\Invoke-GameCommand.ps1 -Route "/component?id=$componentId"

# Явное чтение одного поля или свойства.
.\tools\Invoke-GameCommand.ps1 -Route "/read?id=$componentId&member=enabled"

# Пример вызова метода без изменения состояния.
$body = @{ id = $objectId; method = 'GetInstanceID'; arguments = @() } | ConvertTo-Json -Compress
.\tools\Invoke-GameCommand.ps1 -Route '/invoke' -Method POST -Body $body
```

Если чтение вернуло `{referenceId, type}`, это ссылка на вложенный объект:

```powershell
.\tools\Invoke-GameCommand.ps1 -Route "/component?referenceId=$referenceId"
.\tools\Invoke-GameCommand.ps1 -Route "/read?referenceId=$referenceId&member=SomeMember"

$body = @{ referenceId = $referenceId; method = 'SomeMethod'; arguments = @() } | ConvertTo-Json -Compress
.\tools\Invoke-GameCommand.ps1 -Route '/invoke' -Method POST -Body $body
```

`SomeMember` и `SomeMethod` — места для имён из фактического ответа `/component`, а не готовые методы игры. Возвращённый `objectId` обозначает Unity-объект и используется как `id`.

## Составные команды

```powershell
.\tools\Invoke-GameCommand.ps1 -Route '/scripts'
.\tools\Invoke-GameCommand.ps1 -Route '/scripts/run' -Method POST -Body '{"name":"scene-summary"}'

# Штатный выход из игры через Application.Quit().
.\tools\Invoke-GameCommand.ps1 -Route '/scripts/run' -Method POST -Body '{"name":"quit"}'
```

`scene-summary` читает сцены и корневые объекты за один проход главного потока. Новые составные C# команды добавляются в `SceneCommands.Execute` и требуют сборки и перезапуска игры. Компиляция произвольных фрагментов C# и горячая замена DLL не реализованы.

## Клавиатура

`POST /input/key` принимает `{"key":"Tab","frames":3}`. `key` — имя из Unity Input System `Key`, например `Tab`, `Escape`, `Enter`, `LeftCtrl`. `frames` — число кадров до постановки события отпускания, 2–60, по умолчанию 3. Команда не отправляет OS SendInput и не вызывает обработчик инвентаря напрямую: она ставит событие устройства в очередь Unity Input System.

`GET /input/state` возвращает доступность клавиатуры, нажатые клавиши, запланированные отпускания и кадр последней команды. Перед следующим нажатием дождаться пустых `pressed` и `scheduledReleases`. Нельзя рассчитывать только на задержку в миллисекундах: при низком FPS отпускание наступит позже. Ответ `queued` означает постановку события в очередь; результат проверять по UI. Сочетания клавиш и отдельные команды удержания/отпускания пока не реализованы.

Для этой IL2CPP-сборки обычный `QueueStateEvent<KeyboardState>` недоступен из-за отсутствующей AOT-специализации. Реализация использует `StateEvent.From`, меняет значение клавиши в выделенном Unity буфере события и передаёт его необобщённому вызову `InputSystem.QueueEvent`. Указатель используется только для временного буфера события; сканирования адресов или моделей игры нет. Работоспособность подтверждена открытием, закрытием и повторным открытием инвентаря через Tab.

## Выгрузка предметов инвентаря

```powershell
# Настроить путь один раз; настройка переживает перезапуск игры.
'{"outputPath":"C:\\workroot\\Разработка\\Gamedev\\cargo_hunters\\docs\\diagnostics\\inventory-export.json"}' | curl.exe -sS -H "Authorization: Bearer $($connection.token)" -H 'Content-Type: application/json' --data-binary '@-' "$($connection.url)/inventory/settings"

# Снять и записать актуальный JSON. Пустой запрос использует сохранённый путь.
'{}' | curl.exe -sS -H "Authorization: Bearer $($connection.token)" -H 'Content-Type: application/json' --data-binary '@-' "$($connection.url)/inventory/export"
```

`GET /inventory/settings` показывает настроенный путь. `POST /inventory/settings` принимает `{"outputPath":"абсолютный\путь.json"}`; `null` очищает путь. `POST /inventory/export` принимает необязательный `outputPath`. Если путь не передан и не настроен, команда отвечает HTTP 400 с описанием недостающей настройки. Ошибки доступа к файлу возвращаются как JSON с HTTP 500.

Снимок содержит отдельные группы `player`, `equipment`, `warehouse` и `inspected`. Личные предметы и экипировка берутся из аккаунта; склад — из текущего контейнера контроллера страниц хранилища; осматриваемая цель добавляется отдельно, если это другой контейнер. Для склада выгружается выбранный сейчас контейнер страницы. Когда экран персонажа ещё не загружен в этой сессии, команда возвращает HTTP 400 с просьбой открыть инвентарь и повторить запрос.

Каждая запись соответствует отдельному экземпляру предмета и содержит `itemId`, `templateId`, локализованное `name`, `quantity`, источник, позицию и массив `contents`. Записи не объединяются по названию или шаблону. Вложенные контейнеры, предметы в слотах оружия и установленные модули обходятся рекурсивно; сгруппированные патроны в магазине выгружаются отдельными дочерними записями. `itemCount` включает вложенные записи, `topLevelItemCount` — только корневые ячейки. Файл записывается в UTF-8 без BOM, кириллица сохраняется буквами. Для защиты от циклов и слишком больших снимков установлены пределы 20 000 записей и глубины 32.

## Дерево UI и нажатия

```powershell
# Компактный список элементов управления и надписей.
.\tools\Get-GameUi.ps1

# Полное дерево Canvas с координатами и компонентами.
.\tools\Get-GameUi.ps1 -FullTree
.\tools\Invoke-GameCommand.ps1 -Route '/ui/tree?depth=20&limit=2500'

# Включить неактивные объекты или исследовать отдельное поддерево.
.\tools\Get-GameUi.ps1 -IncludeInactive -RootId $objectId

# Нажать элемент из свежего дерева, затем обязательно прочитать новое состояние.
$body = @{ id = $buttonId } | ConvertTo-Json -Compress
.\tools\Invoke-GameCommand.ps1 -Route '/ui/click' -Method POST -Body $body
.\tools\Get-GameUi.ps1
```

`GET /ui/tree` принимает `rootId` (необязательно), `depth` (0–40, по умолчанию 12), `limit` (1–3000, по умолчанию 1500), `includeInactive=true` (по умолчанию false). Ответ содержит размер экрана и дерево `roots[].children`. Узлы содержат ID, путь, тип элемента, текст uGUI/TextMeshPro, компоненты, активность, оценку видимости, доступность Selectable и экранный прямоугольник. Текст кнопки находится в её дочернем Text-узле. `childrenTruncated` обозначает обрезанную ветвь, `truncated` — достижение общего лимита.

Координаты — пиксели Unity с началом в левом нижнем углу. `visibleEstimate` учитывает активность, Canvas, CanvasGroup alpha и пересечение с экраном; не учитывает `enabled` отдельных Graphic/TMP, все варианты масок, culling и перекрывающие окна. У отключённой надписи или элемента за модальным окном может оставаться `visibleEstimate=true`. Для проверки текста использовать `/object` и `/read` свойства `enabled` его TMP/Graphic-компонента. В инвентаре отключённые надписи действительно содержат шаблоны `$999999`, `999/999`, `midle text`.

`POST /ui/click` принимает ID GameObject элемента. Нажатие использует центр его прямоугольника, проверяет RaycastAll и отправляет pointer enter/down/up/click через Unity EventSystem. Прозрачный, неактивный, недоступный или перекрытый элемент отклоняется. Если центр перекрыт, а край виден, команда также откажет; координатный выбор другой точки пока не реализован. UI Toolkit, ввод текста, перетаскивание и клавиатурные события этой командой не поддерживаются.

Нажатие вызывает игровую логику; ответ о доставке событий не гарантирует завершения перехода. Проверять новое дерево и состояние сцены. Проверенный сценарий сохранённого хаба: «ОК» системного сообщения → «Продолжить» → `Shelter_02_TruckStop` → исчезновение загрузочного оверлея и появление `InShelter_View`. `Lobby_Standard_02` обозначает главное меню.

## Контракт и ограничения

- Адрес: `http://127.0.0.1:18741`. Токен генерируется заново при запуске и сохраняется в `BepInEx/config/cargo-dev-bridge.json`. Скрипт клиента сам читает его. Заголовок: `Authorization: Bearer <token>`.
- Запросы без токена получают HTTP 401; запросы с `Origin` отклоняются, CORS не включён. Сервер слушает только IPv4 loopback.
- `/health` обслуживается вне Unity и может отвечать во время загрузки или остановки главного потока. Готовность Unity проверять через `/scenes`; `frame` показывает число выполнений `Update` компонента.
- `/objects` возвращает максимум 300 записей, посещает максимум 5000 узлов и принимает глубину 0–20. При `truncated=true` увеличить глубину либо выбрать `rootId` и продолжить в нужном поддереве. Ограничение глубины также ограничивает область поиска.
- `/object` возвращает до 100 компонентов и 300 непосредственных детей. Снимок может устареть уже на следующем кадре.
- `/component` показывает до 150 полей, 150 свойств и 200 методов. Поля читаются сразу; свойства перечисляются без вызова getter. `/read` явно вызывает getter, если выбрано свойство.
- Поддерживаются публичные экземплярные поля, свойства и методы доступных managed/interop-типов. Непубличные и статические члены, обобщённые методы и автоматический выбор неоднозначной перегрузки не поддерживаются.
- Аргументы `/invoke`: строки, bool, простые числа, enum по имени или числу, null и ссылки `{objectId: ...}` / `{referenceId: ...}` при совместимом типе параметра. Произвольные структуры и коллекции аргументов не преобразуются автоматически.
- Результаты: скаляры, Vector2/Vector3, Quaternion, Color, ссылки на Unity-объекты или вложенные объекты. Граф объектов не сериализуется рекурсивно.
- ID относятся только к текущему процессу; после уничтожения объекта или смены сцены нужно обновить снимок. Кэш вложенных ссылок ограничен 2000 объектами; при очистке старые `referenceId` перестают действовать.
- Очередь ограничена 64 запросами, за кадр исполняется максимум 4 команды. Через 8 секунд ожидающая команда отменяется с HTTP 504. Если команда уже началась, тайм-аут сообщает о неизвестном результате: повторять её без проверки состояния нельзя. Долгий вызванный игровой метод остановит главный поток до своего завершения.
- Одна команда не является транзакцией; вызов игрового метода может менять мир и сейв. Команда выгрузки только читает игровые контейнеры и пишет указанный JSON-файл. Сортировка модом ещё не реализована.

## Сборка и проверка

```powershell
# При закрытой игре.
.\tools\Build-Deploy.ps1

# После запуска через Steam.
.\tools\Test-GameBridge.ps1
.\tools\Test-GameUi.ps1
```

Проверка выполняет чтение состояния, вызывает `GetInstanceID`, запускает `scene-summary`, проверяет отказ без токена и ошибку для неизвестного ID. Инвентарь в проверках не изменяется.
