# Newtonsoft.Json Converter

[English](README.md) | **Русский**

Этот семпл — конвертер Newtonsoft.Json для полей StableRef. С ним поле `StableRef<T>` / `StableRefList<T>` читает и
пишет **ровно тот же JSON, что и такое же поле, объявленное как голый `T` / `List<T>`**, — перевод поля на StableRef не
меняет JSON, поэтому ремоут-конфиги, сохранения и данные с сервера продолжают работать.

Без него Newtonsoft пишет собственные поля обёртки (ID типа и метаданные редактора) вместо значения.

Он идёт семплом, чтобы у самого StableRef не было зависимости от Newtonsoft.

## Что внутри

- `Runtime/` — сборка `SST.StableRef.Newtonsoft` (все платформы):
  - `StableRefJsonConverter` (неймспейс `SST.StableRef.Json`) — конвертер для всех `StableRef<T>` и `StableRefList<T>`.
  - `StableRefJson.DataEquals(a, b, settings)` — одинаковы ли данные двух объектов (см. [Сравнение данных](#сравнение-данных)).
- `Tests/Editor/` — EditMode-тесты (`SST.StableRef.Newtonsoft.Tests`).

## Подключение

1. В проекте нужен Newtonsoft.Json: пакет `com.unity.nuget.newtonsoft-json` или любая `Newtonsoft.Json.dll`. Без него импортированный семпл не скомпилируется.
2. Импортируй семпл: **Window → Package Manager → StableRef → Samples → Newtonsoft.Json Converter → Import**. После обновления StableRef импортируй его заново.
3. Зарегистрируй конвертер там, где собираешь настройки сериализатора:

```csharp
using SST.StableRef.Json;

var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };
settings.Converters.Add(new StableRefJsonConverter());

// { "effect": { "$type": "...", "Damage": 7 }, "Effects": [ ... ] } — как для IEffect / List<IEffect>
var json = JsonConvert.SerializeObject(config, settings);
```

Для полей, которые раньше были `T[]`, создавай его как `new StableRefJsonConverter(replaceLists: true)` — см. [Чтение списка](#чтение-списка).

## Как это работает

### Запись

Пишется только значение, через твой сериализатор, ровно как для голого поля: `$type`, конвертеры для `T` и твои
настройки применяются. Пустая обёртка пишется как `null`, список — как массив значений.

### Чтение поля

Значение читается так же, как Newtonsoft читает голое поле `T` (`$type`, конвертеры, `Populate` работают). `null`
очищает обёртку, но само поле никогда не становится `null`.

### Чтение списка

Каждый элемент читается как `T`. В существующий список элементы дописываются, как в `List<T>`, если только
`ObjectCreationHandling` не `Replace` и конвертер не создан с `replaceLists: true` — тогда содержимое заменяется.
`replaceLists` нужен для полей, которые раньше были массивами: Newtonsoft всегда их заменял, поэтому заполнение
существующего объекта не дублировало элементы.

### JSON, записанный без конвертера

Данные, записанные до подключения конвертера, остаются читаемыми: объект, похожий на сериализованную обёртку,
распознаётся, и из него берётся значение.

### Сравнение данных

`StableRefJson.DataEquals(a, b, settings)` сравнивает данные двух объектов как JSON — например, чтобы применять конфиг
с сервера, только если он изменился. Работает в player-билдах.

```csharp
var incoming = JsonConvert.DeserializeObject<GameConfig>(serverJson, settings);
if (!StableRefJson.DataEquals(current, incoming, settings))
    Apply(incoming);
```

Сначала десериализуй и сравнивай объекты, а не сырой JSON — тогда порядок ключей и значения по умолчанию не считаются
изменениями. Значение другого типа внутри StableRef считается изменением. Сравнивай обычный класс данных, а не
`ScriptableObject`, который его держит.

## Ограничения

- Атрибуты на самом поле-обёртке (`[JsonProperty(TypeNameHandling = ...)]`, `ItemConverterType`) не действуют; задавай их в `JsonSerializerSettings` или на базовом типе.
- После десериализации в ассеты в редакторе вызови `StableRefResync.ResyncObject(asset)` перед сохранением или запусти **Tools → StableRef → Resync All** — см. [Рефлексия и сериализаторы](../../README.ru.md#рефлексия-и-сериализаторы).
