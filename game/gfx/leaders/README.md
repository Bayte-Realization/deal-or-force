# Leader photographs / Фотографии руководителей

The common portrait UI resolves `leader_<name>` through the existing sprite catalogue.
For `Vladimir Putin`, use `leader_vladimir_putin.jpg` in this directory (or a mod's
`gfx/leaders/`). Lowercase letters and digits remain; punctuation and spaces become `_`.
No photo means one neutral white silhouette on grey, never an invented face.

The shipped fallback `leader_anonymous.png` was generated through Creative Claw.
Its provenance is in `anonymous_portrait.source.json`. Override the same sprite in
a mod to replace the global fallback; if it is missing, a small procedural silhouette
still keeps the interface usable. The generated asset is shared, not cloned per person.

Alternatively declare a sprite in a mod's `gfx/*.json`; paths are relative to its `gfx` root:

```json
{"sprites":{"leader_vladimir_putin":"leaders/putin.webp"}}
```

PNG, JPEG and WebP were tested as loose runtime files without an editor import.
Godot also supports BMP and TGA; this is not a promise to decode every raster format
(convert TIFF/GIF to PNG). See the [Godot Image API](https://docs.godotengine.org/en/stable/classes/class_image.html).
Files are local only, at most 32 MiB; decoded images are scaled to at most 512 px on
their long side without altering originals. Imported resources in exports still work.
Reload the graphics catalogue/restart after changing files; its change event clears
the bounded portrait cache. Use images you have permission to distribute.

Общий интерфейс ищет `leader_<имя>` в существующем каталоге спрайтов. Например,
`leader_vladimir_putin.jpg`; можно переопределить через JSON выше. Пути — от корня
`gfx` мода. Без фотографии показывается нейтральный силуэт, а не вымышленное лицо.
Проверены PNG/JPEG/WebP без импорта редактором; TIFF/GIF сначала преобразуйте в PNG.
Только локальные файлы до 32 МиБ; длинная сторона уменьшается до 512 пикселей.
Оригиналы не меняются. После замены файлов перезагрузите каталог/игру.
