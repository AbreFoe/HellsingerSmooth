# Hellsinger Smooth

Мод для **Metal: Hellsinger** (Steam), который повышает FPS и убирает микрофризы на слабом железе, **не трогая игровую механику**: тайминги попадания в бит, ритм и синхронизация с музыкой остаются как в оригинале.

Плагин для BepInEx 6 (IL2CPP). Как он устроен изнутри, описано в [docs/how-it-works.md](docs/how-it-works.md).

## Результат

GTX 1050 Ti, i5-8500, 1920×1080, минимальные настройки в игре:

| | Средний FPS | 1% low |
|---|---|---|
| Без масштаба рендера (v1.1) | 40–48 | 28–37 |
| С модом v1.2 (рендер 80% + FSR) | 55–59 (упор в лимит игры 60) | 39–48 |

## Что делает мод

- **Масштаб рендера с апскейлом AMD FSR 1.0.** 3D-сцена рисуется в меньшем разрешении, интерфейс остаётся чётким. По умолчанию масштаб **динамический (70–90%)**: в тяжёлых боях разрешение снижается, чтобы держать FPS, в спокойные моменты поднимается.
- **Отключает дорогие эффекты HDRP:** motion blur, глубину резкости, хроматическую аберрацию, зерно, искажение линзы, SSR, SSGI, контактные тени, объёмные облака. Опционально: объёмный туман, SSAO, дальность и каскады теней.
- **Сглаживает подгрузку текстур и мешей**, чтобы она шла мелкими порциями, а не рывками.
- **Опционально:** лимит FPS выше 60 (для мониторов 75–144 Гц), эксклюзивный полноэкранный режим, LOD, качество текстур.
- **Пишет статистику FPS в лог**, чтобы эффект можно было честно сравнить.

Мод не меняет код игры, время, физику, звук и ввод. Подробности в разделе [«Главный принцип»](docs/how-it-works.md#2-главный-принцип-не-трогать-механику).

## Установка

1. Скачайте архив `HellsingerSmooth-vX.Y.Z.zip` со страницы **Releases**. В нём уже есть BepInEx.
2. Steam → Metal: Hellsinger → ПКМ → Управление → **Просмотреть локальные файлы**.
3. Распакуйте всё содержимое архива в эту папку, рядом с `Metal.exe`.
4. Запустите игру. **Первый запуск идёт 1–5 минут**: BepInEx один раз готовит файлы для модов.

Если BepInEx 6 IL2CPP у вас уже стоит, достаточно архива `...-plugin.zip`: он кладёт только `BepInEx/plugins/HellsingerSmooth/HellsingerSmooth.dll`.

**Удаление:** удалите `BepInEx`, `dotnet`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` из папки игры или поставьте `Enabled = false` в конфиге.

## Настройка

Конфиг `BepInEx/config/henry.hellsinger.smooth.cfg` появляется после первого запуска. Меняется в Блокноте, применяется при следующем запуске.

| Секция | Параметр | По умолчанию | Описание |
|---|---|---|---|
| General | `Enabled` | `true` | Главный выключатель |
| Resolution | `RenderScaleMode` | `Dynamic` | `Dynamic` / `Fixed` / `Off` |
| | `DynamicMinScale` / `DynamicMaxScale` | `70` / `90` | Границы динамического масштаба, % |
| | `DynamicTargetFps` | `0` | Какой FPS держать (0 = лимит игры) |
| | `RenderScale` | `80` | Масштаб для режима `Fixed`, % |
| | `Upscaler` | `FSR` | `FSR`, `CAS`, `CatmullRom`, `TAAU` |
| | `FullscreenMode` | `-1` | `-1` не трогать, `0` эксклюзивный, `1` окно без рамки, `3` окно |
| FramePacing | `TargetFrameRate` | `0` | Лимит FPS (0 = как в игре, 60) |
| | `VSyncCount` | `-1` | `-1` не трогать, `0` выкл, `1` вкл |
| | `MaxQueuedFrames` | `-1` | Очередь кадров (`1` = меньше задержка) |
| Streaming | `AsyncUploadTimeSliceMs` | `4` | Время на подгрузку ресурсов за кадр, мс |
| | `AsyncUploadBufferSizeMB` | `32` | Буфер подгрузки, МБ |
| | `AsyncUploadPersistentBuffer` | `true` | Не освобождать буфер подгрузки |
| | `TextureQualityReduction` | `0` | `1` = текстуры в 2 раза меньше (если мало видеопамяти) |
| Geometry | `LodBias` | `0` | `0` не трогать; меньше = проще модели вдали |
| | `MaximumLODLevel` | `-1` | `1` = не использовать самый детальный LOD |
| | `SkinWeights` | `0` | Кости на вершину у анимированных моделей (`2` = дешевле) |
| Effects | `DisableMotionBlur`, `DisableDepthOfField`, `DisableChromaticAberration`, `DisableFilmGrain`, `DisableLensDistortion` | `true` | Постобработка |
| | `DisableScreenSpaceReflections`, `DisableScreenSpaceGI`, `DisableContactShadows`, `DisableVolumetricClouds` | `true` | Дорогие эффекты освещения |
| | `DisableBloom`, `DisableVolumetricFog`, `DisableAmbientOcclusion` | `false` | Сильно меняют картинку, включайте при нехватке FPS |
| | `MaxShadowDistance` | `0` | Дальность теней, м (0 = не трогать) |
| | `ShadowCascades` | `0` | Каскады теней от солнца (0 = не трогать) |
| Diagnostics | `StatsIntervalSeconds` | `30` | Как часто писать `[Stats]` в лог (0 = выкл) |

## Проверка и отладка

В `BepInEx/LogOutput.log` после пары минут на уровне должны быть строки:

```
[Info   :Hellsinger Smooth] Per-frame hook: injected MonoBehaviour
[Info   :Hellsinger Smooth] Render scale 80% with upscaler EdgeAdaptiveScalingUpres (Dynamic)
[Info   :Hellsinger Smooth] [Stats] avg 58 FPS | 1% low 41 FPS | worst 27 ms | hitches 0 in 1740 frames | render 75-90% (avg 86%)
```

Строки `... not found in this game build, skipped` не страшны: это значит, что в этой версии игры такого эффекта нет.

Советы без мода: в панели NVIDIA поставьте «Размер кэша шейдеров» на 10 ГБ или «Без ограничений», это уменьшает фризы при первом появлении эффектов.

## Сборка из исходников

Нужен .NET SDK 6 или новее.

1. Скачайте [BepInEx 6.0.0-pre.2 IL2CPP win-x64](https://github.com/BepInEx/BepInEx/releases/tag/v6.0.0-pre.2) и скопируйте его папку `BepInEx/core` в `lib/BepInEx/core` в корне репозитория. Папка `lib/` в git не попадает.
2. Соберите:
   ```
   dotnet build src/HellsingerSmooth -c Release
   ```
   Другой путь к библиотекам можно передать так: `-p:BepInExCore=<путь к BepInEx/core>`.
3. Готовый файл: `src/HellsingerSmooth/bin/Release/net6.0/HellsingerSmooth.dll`. Его нужно положить в `BepInEx/plugins/HellsingerSmooth/` в папке игры.

Файлы игры для сборки не нужны: все типы Unity и HDRP плагин находит во время работы ([почему так](docs/how-it-works.md#4-работа-без-файлов-игры-рефлексия)). В репозитории есть workflow GitHub Actions (`.github/workflows/build.yml`), который делает то же самое автоматически при каждом push.

## Структура

```
src/HellsingerSmooth/     плагин
  Plugin.cs               точка входа, конфиг, покадровый вызов
  ResolutionTweaks.cs     масштаб рендера и динамическое разрешение
  VolumeTweaks.cs         эффекты HDRP в профилях Volume
  QualityTweaks.cs        QualitySettings, лимит FPS, режим экрана
  FrameStats.cs           статистика FPS в лог
  Reflect.cs              рефлексия по interop-сборкам
src/RefStubs/             заглушки MonoBehaviour только для компиляции
docs/how-it-works.md      подробное описание работы мода
```

## Благодарности

- [BepInEx](https://github.com/BepInEx/BepInEx) и [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop): загрузчик модов (LGPL-2.1, распространяется в релизном архиве без изменений).
- [HellsingerVR](https://github.com/LivingFray/HellsingerVR): подтвердил, что BepInEx 6 работает с этой игрой, и показал подход к эффектам HDRP.
- Metal: Hellsinger — игра студии The Outsiders, издатель Funcom. Мод неофициальный.

## Лицензия

[MIT](LICENSE)
