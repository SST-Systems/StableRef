<img src="Documentation~/banner.png" width="900" alt="StableRef">

[![release](https://img.shields.io/github/v/release/SST-Systems/StableRef)](../../releases)
[![release date](https://img.shields.io/github/release-date/SST-Systems/StableRef)](../../releases)
[![last commit](https://img.shields.io/github/last-commit/SST-Systems/StableRef)](../../commits)
[![license](https://img.shields.io/github/license/SST-Systems/StableRef)](LICENSE.md)

[English](README.md) | **Русский**

---

Удобная и надёжная обёртка над `[SerializeReference]` в Unity.

StableRef делает работу с полиморфными сериализованными ссылками стабильной и комфортной: поиск по типам в селекторе инспектора, безопасные копирование/вставка и инструменты редактора для поиска и починки ссылок. Вдобавок ссылки переживают переименование классов — рядом с объектом хранится стабильный строковый ID типа, поэтому переименование или перемещение класса не разрушает уже сохранённые данные.

## Содержание

<details>
<summary>Развернуть</summary>

- [Установка](#установка)
- [Проблема, которую это решает](#проблема-которую-это-решает)
- [Классы и атрибуты](#классы-и-атрибуты)
- [Использование](#использование)
  - [Объявление стабильного типа](#объявление-стабильного-типа)
  - [StableRef\<T\> в поле](#stablereft-в-поле)
  - [StableRefList\<T\>](#stablereflistt)
  - [Копирование владельца: MemberwiseClone](#копирование-владельца-memberwiseclone)
  - [Рефлексия и сериализаторы](#рефлексия-и-сериализаторы)
  - [Обобщённые типы значений](#обобщённые-типы-значений)
  - [Селектор без обёртки: \[RefSelector\]](#селектор-без-обёртки-refselector)
  - [Навигация с клавиатуры в селекторе](#навигация-с-клавиатуры-в-селекторе)
- [Автоматическая генерация ID](#автоматическая-генерация-id)
  - [Типы, которые нельзя править: RefTypeIdFor](#типы-которые-нельзя-править-reftypeidfor)
- [Newtonsoft.Json](#newtonsoftjson)
- [Инструменты редактора](#инструменты-редактора)
- [Интеграция с инспектором](#интеграция-с-инспектором)
- [Копирование и вставка](#копирование-и-вставка)
- [Лицензия](#лицензия)

</details>

---

## Установка

1. **.unitypackage** — [Releases](https://github.com/SST-Systems/StableRef/releases)
2. **UPM** — `Window → Package Manager` → `+` → `Add package from git URL`:
   `https://github.com/SST-Systems/StableRef.git`
   Добавь `#тег` в конец URL для фиксации версии.
3. **Вручную** — склонируй или скачай, скопируй в `Assets/`.

Unity 2021.3+

---

## Проблема, которую это решает

Стандартный `[SerializeReference]` хранит полное assembly-qualified имя типа. Если переименовать или переместить класс, Unity теряет ссылку и поле становится `null`. `StableRef` разделяет сериализованную идентичность и имя класса — тип получает постоянный ID через `[RefTypeId]`, и можно переименовывать его свободно.

---

## Классы и атрибуты

| Тип | Назначение |
|---|---|
| `StableRef<T>` | Сериализуемая обёртка для одиночной полиморфной ссылки типа `T`. |
| `StableRefList<T>` | Сериализуемый список значений, переживающих переименования: `IList<T>` самих значений, каждое хранится в своей записи `StableRef<T>`. |
| `[RefTypeId("id")]` | Присваивает классу постоянный ID. Переименовывай класс как угодно — Unity его найдёт. |
| `[assembly: RefTypeIdFor(typeof(T), "id")]` | То же для типа, который нельзя править, — из другого пакета, DLL, сгенерированного кода. |
| `[RefCategory("Path")]` | Группирует тип под подменю в инспекторном селекторе. |
| `[RefSelector]` | Только для редактора: тот же селектор на обычном поле `[SerializeReference]`, без обёртки и без защиты от переименований. |

---

## Использование

### Объявление стабильного типа

```csharp
[Serializable]
[RefTypeId("my-package.damage-on-hit")]
[RefCategory("Combat")]
public class DamageOnHit : IEffect
{
    public int Amount;
}
```

Значение `[RefTypeId]` должно быть уникальным в проекте. Используй строки с неймспейсом, чтобы избежать коллизий. Атрибут **не наследуется**: классу-наследнику `DamageOnHit` нужен свой `[RefTypeId]` (или свой файл скрипта, см. [Автоматическая генерация ID](#автоматическая-генерация-id)) — иначе у него нет стабильного ID и селектор его не предлагает.

### StableRef\<T\> в поле

```csharp
[Serializable]
public class ItemConfig : ScriptableObject
{
    public StableRef<IEffect> OnPickup;
}

// Чтение и запись
if (config.OnPickup.HasValue)
    config.OnPickup.Value.Apply();

config.OnPickup.Set(new DamageOnHit { Amount = 5 });
config.OnPickup = new StableRef<IEffect>(new DamageOnHit { Amount = 5 });
```

`Set` сохраняет стабильный ID записи, если новое значение того же типа, что и старое, и сбрасывает метаданные, если тип сменился, — ID чужого типа не остаётся. Прямая запись в `.Value` тоже работает, но оставляет метаданные как были.

Неявных преобразований между `StableRef<T>` и `T` нет: C# не применяет пользовательские преобразования из интерфейсов и в интерфейсы, а `T` обычно интерфейс, поэтому они работали бы для одних полей и не работали для других. Используй `.Value` и конструктор.

### StableRefList\<T\>

```csharp
[Serializable]
public class AbilityConfig : ScriptableObject
{
    public StableRefList<IEffect> Effects;
}

// Перебор — по значениям, без аллокаций
foreach (var effect in config.Effects)
    effect?.Apply();
```

`StableRefList<T>` — это `IList<T>` и `IReadOnlyList<T>` самих значений: индексатор, `foreach` и LINQ (`OfType`, `Any`, `Select`, ...) работают с `T`, а список можно передавать туда, где ждут `IEnumerable<T>` / `IReadOnlyList<T>`. `foreach` идёт через struct-енумератор и не аллоцирует. API в духе `List<T>` тоже есть — `Add`, `AddRange`, `Insert`, `Remove`, `RemoveAt`, `RemoveAll`, `Clear`, `Contains`, `IndexOf`, `Find`, `ToArray` и т.д.:

```csharp
config.Effects.Add(new DamageOnHit { Amount = 5 });
config.Effects.RemoveAll(e => e is DamageOnHit);
var damage = config.Effects.OfType<DamageOnHit>().Sum(d => d.Amount);
config.Effects[0] = new Heal();
var rewards = new StableRefList<IReward>(otherRewards);
```

Каждое значение хранится в своей записи `StableRef<T>` со стабильным ID и снапшотом; `Items` открывает эти записи (`List<StableRef<T>>`). Присваивание через индексатор переиспользует запись, как `Set`: значение того же типа сохраняет стабильный ID, значение другого типа его сбрасывает.

Если список формируется **из editor-скрипта**, а не через инспектор, вызови `StableRefResync.ResyncObject(asset)` перед сохранением, чтобы новые элементы получили стабильные ID и снапшоты (в инспекторе это происходит автоматически при отрисовке поля) — см. [Рефлексия и сериализаторы](#рефлексия-и-сериализаторы).

<p align="center">
  <img src="Documentation~/inspector.gif" alt="Добавление типа через типизированный дропдаун" width="580">
</p>

### Копирование владельца: MemberwiseClone

`StableRef<T>` и `StableRefList<T>` — классы. Владелец, склонированный через `MemberwiseClone`, делит их с оригиналом, и `clone.OnPickup.Value = x` меняет и оригинал — в отличие от голого поля `[SerializeReference] T`, где та же строка меняет только клон. Дай клону собственные обёртки:

```csharp
public Effect ShallowClone()
{
    var clone = (Effect)MemberwiseClone();
    clone.Modifier = Modifier.ShallowCopy();   // StableRef<T>: новая обёртка, тот же экземпляр значения
    clone.Children = Children.ShallowCopy();   // StableRefList<T>: новые записи, те же экземпляры значений
    return clone;
}
```

Глубокое копирование значений — на твоей стороне: `JsonUtility` не умеет `[SerializeReference]`.

### Рефлексия и сериализаторы

Код, который знает тип поля только во время выполнения (загрузчики конфигов на рефлексии, свои сериализаторы), может читать и писать StableRef-поля без дженерик-аргумента:

```csharp
if (StableRefReflection.IsStableRef(field.FieldType, out var valueType))
{
    var entry = (StableRefBase)(field.GetValue(owner) ?? Activator.CreateInstance(field.FieldType));
    entry.BoxedValue = Convert(cell, valueType);          // с проверкой типа, при несовпадении — ArgumentException
    field.SetValue(owner, entry);
}
else if (StableRefReflection.IsStableRefList(field.FieldType, out var elementType))
{
    var list = (StableRefListBase)(field.GetValue(owner) ?? Activator.CreateInstance(field.FieldType));
    list.SetBoxedValues(ConvertArray(cell, elementType)); // проверяет все элементы до изменения списка
    field.SetValue(owner, list);
}
```

У `StableRefBase` есть `ValueBaseType`, `BoxedValue`, `HasValue`; у `StableRefListBase` — `ElementType`, `Count`, `GetBoxed`, `SetBoxed`, `AddBoxed`, `SetBoxedValues`, `Clear`. `field.SetValue(owner, value)` с голым значением не сработает — рефлексия не применяет преобразования.

`BoxedValue`, `SetBoxed` и `SetBoxedValues` переиспользуют записи, как `Set` (`SetBoxedValues` — по индексу), поэтому запись конфига, в котором типы не поменялись, сохраняет стабильные ID.

**Запись в ассеты в редакторе.** Код не может снять снапшот, а для значения нового типа — и проставить ID: это делает инспектор при отрисовке поля. Инструмент, который пишет значения в ассеты в редакторе (импорт конфигов, бейкер, пересериализация), должен вызывать `StableRefResync.ResyncObject(asset)` для каждого изменённого объекта перед сохранением или запускать после себя **Tools → StableRef → Resync All** (см. [Инструменты редактора](#инструменты-редактора)). Иначе снапшот так и будет описывать прежние значения, и после переименования класса восстановятся именно они.

### Обобщённые типы значений

Селектор поддерживает и закрытые generic-типы элемента. Для поля вида `StableRefList<ICondition<Unit>>` предлагаются открытые generic-определения, которые ему подходят (например, `All<TContext>`, `Any<TContext>`), закрытые аргументом самого поля (`All<Unit>`). Каждому типу-аргументу нужен свой стабильный ID — свой файл или `[RefTypeId]`, как и любому другому типу StableRef.

### Селектор без обёртки: [RefSelector]

Если нужен только селектор в инспекторе — без типа-обёртки, без сохранённого ID, без лишних данных в сериализации и в билде — пометь обычное поле `[SerializeReference]` атрибутом `[RefSelector]`:

```csharp
using SST.StableRef;

public class EffectAuthoring : MonoBehaviour
{
    [SerializeReference, RefSelector] private IEffect _onPickup;
    [SerializeReference, RefSelector] private List<IEffect> _effects;
}
```

Поле остаётся обычным `[SerializeReference]`: код читает `_onPickup` напрямую (без `.Value`), а атрибут помечен `[Conditional("UNITY_EDITOR")]`, поэтому в player-билд он даже не попадает. Это удобно для authoring/baking-кода ECS, для существующих полей `[SerializeReference]`, которые не хочется мигрировать, и для больших списков, где метаданные на каждый элемент не окупаются. Селектор предлагает все создаваемые типы, включая типы без стабильного ID; копирование и вставка через контекстное меню работают как обычно.

> **На свой страх и риск.** У полей `[RefSelector]` **нет защиты от переименований**: переименование или перенос класса значения ломает ссылку точно так же, как у голого `[SerializeReference]`. Они намеренно **не** охватываются **Find Usages** и **Fix Missing Types**. Поле, чей класс пропал, просто показывает пустой селектор (`None`) и заменяется без подтверждения. Чтобы безопасно переименовать класс, добавь ему юнитевский `[MovedFrom]` (`UnityEngine.Scripting.APIUpdating`). Если нужны ссылки, переживающие рефакторинг и видимые инструментам, используй `StableRef<T>`.

### Навигация с клавиатуры в селекторе

Селектором можно пользоваться без мыши; стрелки работают так же, как в окнах Hierarchy и Project в Unity. Он открывается с выделенным текущим типом, а фокус остаётся в поле поиска, так что фильтровать можно в любой момент.

| Клавиша | Действие |
|---|---|
| `↑` / `↓`, `PageUp` / `PageDown` | Перемещение выделения по типам и категориям |
| `→` | Раскрыть выделенную свёрнутую категорию; иначе — перейти вниз к следующей категории |
| `←` | Свернуть выделенную раскрытую категорию; иначе — перейти к родительской категории (на верхнем уровне — к предыдущей) |
| `Alt` + `→` / `←` | Раскрыть / свернуть категорию вместе со всеми вложенными |
| `Enter` | Выбрать выделенный тип (или раскрыть / свернуть выделенную категорию) |
| `Home` / `End` | Первая / последняя строка |
| `Esc` | Закрыть без изменений |

Пока введён поиск, список плоский: `Enter` выбирает подсвеченное совпадение (по умолчанию первое), а `←` / `→` / `Home` / `End` редактируют текст поиска.

---

## Автоматическая генерация ID

`[RefTypeId]` — опционален. Если атрибут не указан, StableRef автоматически использует **MonoScript GUID** (значение поля `guid` из `.meta`-файла скрипта) в качестве стабильного идентификатора. Это значит:

- **Переименование класса** — безопасно. GUID привязан к файлу, а не к имени класса.
- **Переименование или перемещение файла скрипта** — тоже безопасно. Meta-файл переезжает вместе с ассетом, GUID не меняется.
- **Удаление и повторное создание файла** — ссылка теряется (резолвится в `null`), но ошибка обрабатывается контролируемо. Проект продолжит работать; потерянный тип отобразится в Fix Missing Types.

Для типов, которые планируется активно рефакторить, явный `[RefTypeId]` надёжнее — он выживает даже при удалении и пересоздании файла скрипта.

Переход типа с авто-ID на явный `[RefTypeId]` безопасен и **не** создаёт missing-ссылок. Кастомный ID имеет приоритет, а существующие ссылки мигрируют автоматически — сохранённый ID переписывается с MonoScript-GUID на твой кастомный при следующей отрисовке поля в инспекторе или сразу во всех ассетах через **Tools → StableRef → Resync All**. До этого старый GUID продолжает резолвиться (файл скрипта не менялся), поэтому ничего не теряется. Если добавляешь атрибут именно ради подготовки к серьёзному рефактору (удаление и пересоздание файла) — сначала запусти Resync All, чтобы «застолбить» новый ID.

> **Важно:** нельзя размещать несколько классов в одном файле скрипта. Автоматическая генерация ID опирается на MonoScript GUID, который присваивается файлу, а не классу, — при нескольких классах в одном файле генерация ID будет работать некорректно.

### Типы, которые нельзя править: RefTypeIdFor

На тип из другого пакета, скомпилированной DLL или source generator'а нельзя повесить `[RefTypeId]`, и часто у него нет своего MonoScript. Назначь ему ID на уровне сборки — в любой сборке своего проекта:

```csharp
using SST.StableRef;

[assembly: RefTypeIdFor(typeof(ThirdParty.Effects.Burn), "my-game.third-party.burn")]
[assembly: RefTypeIdFor(typeof(ThirdParty.Conditions.All<>), "my-game.third-party.all")]
```

- **Приоритет:** `[RefTypeId]` на типе → `RefTypeIdFor` → MonoScript GUID. Назначить ID типу, у которого уже есть `[RefTypeId]`, — ошибка; побеждает атрибут.
- **Generic:** назначай ID открытому определению (`All<>`); закрытые типы, как обычно, получают составной ID из определения и аргументов.
- **Уникальность:** правила те же, что у `[RefTypeId]`. ID, использованный дважды (двумя назначениями или назначением и `[RefTypeId]`), и тип с двумя ID логируются как ошибка при загрузке.
- Перевод такого типа с GUID на назначенный ID безопасен — так же, как добавление `[RefTypeId]` (см. выше).
- Атрибут — только метаданные для редактора (`[Conditional("UNITY_EDITOR")]`), в player-билд ничего не попадает.

Для `partial`-класса из source generator'а можно также повесить `[RefTypeId]` на свою `partial`-часть этого класса.

---

## Newtonsoft.Json

По умолчанию Newtonsoft.Json видит `StableRef<T>` как обычный класс: пишет поля обёртки (`TypeId` и `Value`, в редакторе ещё `ValuesData`, `ObjectRefs` и остальной снапшот) вместо значения, а значение, записанное для голого поля, при чтении теряет. Семпл **Newtonsoft.Json Converter** добавляет конвертер, с которым поле-обёртка читает и пишет **ровно тот же JSON, что и то же поле, объявленное как голый `T` / `List<T>`**.

Импортируй его через **Window → Package Manager → StableRef → Samples → Newtonsoft.Json Converter → Import**. Он копируется в `Assets/Samples/StableRef/<version>/Newtonsoft.Json Converter/` как сборка `SST.StableRef.Newtonsoft` вместе со своими EditMode-тестами. Если StableRef установлен копированием в `Assets/`, скопируй папку `Samples~/NewtonsoftJson` из репозитория. После обновления StableRef импортируй семпл заново.

```csharp
using SST.StableRef.Json;

var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };
settings.Converters.Add(new StableRefJsonConverter());

// { "effect": { "$type": "...", "Damage": 7 }, "Effects": [ ... ] } — как для IEffect / List<IEffect>
var json = JsonConvert.SerializeObject(config, settings);
```

- Семпл требует Newtonsoft.Json в проекте: пакет `com.unity.nuget.newtonsoft-json` или любую `Newtonsoft.Json.dll` (например, пришедшую вместе с каким-нибудь SDK). Без неё импортированный семпл не скомпилируется — установи Newtonsoft или удали папку семпла.
- Значение идёт через твой сериализатор с `T` в качестве объявленного типа, поэтому `TypeNameHandling.Auto`, конвертеры, зарегистрированные для `T` (например, своя схема `$type` для интерфейса), и `JsonSerializer.Populate` в существующее значение работают как для голого поля. Список следует `ObjectCreationHandling`, как `List<T>`: дописывает в существующий список, если не задан `Replace`.
- **Поля, которые раньше были `T[]`:** массивы Newtonsoft всегда заменяет целиком, поэтому `Populate` в существующий объект их элементы не дублировал. Чтобы так и осталось, создай конвертер как `new StableRefJsonConverter(replaceLists: true)`: списки тогда всегда заменяются, а не дописываются.
- Значения присваиваются как через `Set` / `SetBoxedValues`: записи переиспользуются, и запись, значение которой не сменило тип, сохраняет стабильный ID. После десериализации в ассеты в редакторе вызывай `StableRefResync.ResyncObject` (см. [Рефлексия и сериализаторы](#рефлексия-и-сериализаторы)).
- `null` очищает обёртку или список — в отличие от голого поля, экземпляр в поле остаётся и после загрузки никогда не бывает `null`.
- Настройки на самом поле-обёртке (`[JsonProperty(TypeNameHandling = ...)]`, `ItemTypeNameHandling`, `ItemConverterType`) до конвертера не доходят и не применяются — задавай их в `JsonSerializerSettings` или на базовом типе.
- JSON, записанный без конвертера (собственные поля обёртки — из редактора или из билда), тоже читается, как и списки 3.x, записанные массивом обёрток. Объект считается обёрткой, если в нём есть `TypeId` и `Value` и нет других полей, кроме полей обёртки.

---

## Инструменты редактора

Все инструменты доступны через **Tools → StableRef** в строке меню Unity.

**Что сканируют инструменты:** префабы и ScriptableObject-ассеты в `Assets/` — включая ScriptableObject'ы, лежащие под-ассетами внутри другого файла (узлы графов, клипы Timeline, `StateMachineBehaviour`), которые показываются под своим файлом в группе *Scriptable Objects* как `Имя (Тип)`, — и уже открытые сцены. Пакеты и закрытые сцены не сканируются.

**Find Usages** (`Tools/StableRef/Find Usages`) — сканирует префабы, открытые сцены и скриптовые объекты и показывает все места, где используется выбранный тип. Также доступно через правый клик на ассете скрипта: `Assets/Find StableRef Usages`.

<p align="center">
  <img src="Documentation~/find-usages.gif" alt="Окно Find Usages" width="640">
</p>

**Fix Missing Types** (`Tools/StableRef/Fix Missing Types`) — сканирует проект в поисках StableRef-полей, чей ID больше не соответствует ни одному известному типу. Удобно после рефакторинга — позволяет найти сломанные ссылки до того, как они превратятся в молчаливую потерю данных.

<p align="center">
  <img src="Documentation~/fix-missing.gif" alt="Окно Fix Missing Types" width="560">
</p>

**Resync All** (`Tools/StableRef/Resync All`) — приводит стабильный ID и снапшот каждой StableRef-записи в ScriptableObject'ах и префабах из `Assets/` и в открытых сценах в соответствие с её текущим значением. Инспектор делает это сам, пока ты редактируешь; запускай Resync All после того, как значения записал код — импортёры, генераторы, `field.Value = ...` в editor-скриптах, — чтобы последующее переименование их классов восстанавливалось (запись без ID восстановить нельзя, а запись с ID прежнего значения восстановится не тем типом). Сами значения не меняются, missing-записи остаются для Fix Missing Types. Изменённые ассеты сохраняются, изменённые сцены помечаются dirty. В инстансах и вариантах префабов затрагиваются только переопределённые записи, поэтому override'ов ради метаданных не появляется. Типы значений, у которых всё ещё нет стабильного ID, выводятся в консоль. Из кода: `StableRefResync.ResyncObject(target)` / `StableRefEntry.Refresh(entry)`.

**Metadata Size Report** (`Tools/StableRef/Metadata Size Report`) — оценивает объём метаданных StableRef: что попадает в player-билд (стабильные ID — сериализованные байты и managed-память при загрузке) и что остаётся только в ассетах (отображаемые имена и снапшоты), на запись и для самых тяжёлых ассетов. Считается по бинарному формату Unity (строка — 4 байта длины плюс UTF-8 с выравниванием до 4, ссылка на объект — 12 байт; в managed-куче строка — около 22 + 2 байта на символ), а не замеряется по билду, и выводится в консоль.

**Метаданные в билде.** В player-билд попадают только стабильный ID (`TypeId`) и само значение. Отображаемое имя и снапшот (`TypeDisplayName`, `ObjectRefs`, `ObjectRefPaths`, `ValuesData`) — поля только для редактора: они хранятся в ассетах для восстановления, копирования/вставки и инструментов, а Unity не включает их в билд, поэтому ни размера, ни памяти они там не занимают. Код, который их читает или пишет, должен быть внутри `#if UNITY_EDITOR`. AssetBundles, собранные с `DisableWriteTypeTree`, пересобирай вместе с плеером — как при любом изменении сериализуемых полей.

**Что восстанавливает recovery.** Рядом с managed reference каждая запись хранит снапшот своего значения. После переименования или пересоздания типа восстановление возвращает вложенные структуры, массивы и списки, скрытые сериализованные поля, ссылки на `UnityEngine.Object` в любом месте значения и вложенные внутрь StableRef-записи (чинятся проходами). Не сохраняются — возвращаются дефолтами нового экземпляра: `AnimationCurve`, `Gradient`, `Hash128`, `ExposedReference`, fixed-буферы.

Записи, чей ID не резолвится, **Fix All пропускает и сохраняет** (восстанови тип или его `[RefTypeId]` и запусти снова); удалить такую осознанно — выбрать None (или другой тип) в её селекторе, **Set to None** в меню по правому клику или удалить элемент списка. Сломанная запись не блокирует ни своё поле, ни свой `StableRefList`, а замена сначала просит подтверждения: класс может пропасть и ненадолго (ошибки компиляции, переключение веток).

---

## Интеграция с инспектором

Для проектов со своим инспекторным фреймворком (IMGUI):

- **Отрисовка полей значения.** По умолчанию поля внутри значения StableRef рисуются через `EditorGUI.PropertyField`. Реализуй `IStableRefChildrenDrawer` и присвой его `StableRefDrawing.ChildrenDrawer` (из `[InitializeOnLoad]`-типа), чтобы пустить их через свой фреймворк — тогда его атрибуты работают и внутри значений StableRef. Он вызывается только для раскрытого поля со значением одного типа; селектор, missing-записи, multi-object editing и контекстное меню остаются за StableRef. Если высота того, что рисует твой drawer, меняется из-за его собственного состояния (foldout'ы, вкладки, условные поля), вызывай `StableRefDrawing.InvalidateLayout()` — списки StableRef кэшируют свою высоту, и без этого элементы будут наезжать друг на друга до смены выделения. Меняй значения через контролы `EditorGUI` по property, чтобы выставлялся `GUI.changed`, — по этому сигналу StableRef обновляет снапшот записи для восстановления; значение, записанное в объект напрямую, сохранит старый снапшот до следующего изменения или Resync.
- **Имена, категории, подсказки, цвета и порядок типов в селекторе.** Реализуй `IRefTypeMetadataProvider` (конструктор без параметров, находится автоматически), чтобы подключить свои атрибуты. Провайдеры опрашиваются по `Order`; незаполненные поля берутся из имени типа и `[RefCategory]`. `SortOrder` задаёт порядок типов внутри категории (меньше — выше, при равенстве — по имени).
- **Код, который ищет поля по имени.** `StableRefEditorUtility.GetValueProperty(property)` возвращает managed reference и для `StableRef<T>` (его `Value`), и для обычного поля `[SerializeReference]`, поэтому drawer, читающий `managedReferenceValue`, продолжит работать, когда поле станет `StableRef<T>`.

```csharp
[InitializeOnLoad]
static class MyInspectorStableRefBridge
{
    static MyInspectorStableRefBridge() => StableRefDrawing.ChildrenDrawer = new MyChildrenDrawer();
}

sealed class MyChildrenDrawer : IStableRefChildrenDrawer
{
    public float GetChildrenHeight(SerializedProperty value) => MyInspector.GetChildrenHeight(value);
    public void DrawChildren(Rect position, SerializedProperty value) => MyInspector.DrawChildren(position, value);
}

// Runtime-сборка — свой атрибут на типах значений
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EffectInfoAttribute : Attribute
{
    public readonly string Name;
    public readonly string Category;
    public string Tooltip;
    public int Priority;

    public EffectInfoAttribute(string name, string category = null)
    {
        Name = name;
        Category = category;
    }
}

[Serializable, RefTypeId("my-game.burn")]
[EffectInfo("Burn", "Damage/Over Time", Tooltip = "Наносит урон каждую секунду", Priority = 10)]
public class Burn : IEffect { public float DamagePerSecond; }

// Editor-сборка — отображаем его в селекторе
sealed class EffectInfoMetadata : IRefTypeMetadataProvider
{
    public int Order => 0;

    public bool TryGetMetadata(Type type, out RefTypeMetadata metadata)
    {
        var info = type.GetCustomAttribute<EffectInfoAttribute>();
        metadata = info == null
            ? default
            : new RefTypeMetadata
            {
                DisplayName = info.Name,
                Category = info.Category,
                Tooltip = info.Tooltip,
                SortOrder = -info.Priority   // больший приоритет — выше
            };
        return info != null;
    }
}
```

---

## Копирование и вставка

Встроенные в Unity **Copy Component** / **Paste Component Values** не всегда корректно обрабатывают данные `[SerializeReference]` при переносе между разными сериализованными документами (например, со сцены в префаб). Это может оставить повреждённую запись в `managedReferences`, из-за которой в консоли появляется ошибка вида:

```
Could not update a managed instance value at property path 'managedReferences[...]', with value '...'
```

Эта ошибка может не пропадать даже после перезапуска редактора и **не** исчезает при откате (revert) компонента — повреждение уже записано в сериализованный файл.

Чтобы безопасно переносить значения `StableRef<T>` / `StableRefList<T>`, используй встроенное контекстное меню по правому клику вместо стандартного копирования/вставки компонента. ПКМ по элементу списка открывает меню **этого элемента** (в `StableRefList<T>` — в любом месте его строки), ПКМ по заголовку списка — меню всего списка. Те же команды элемента работают в `List<StableRef<T>>`, `StableRef<T>[]` и списках с `[RefSelector]` — там строки рисует сам Unity, поэтому кликай по полю элемента:

| Пункт меню | Где вызывать (ПКМ) | Что делает |
|---|---|---|
| `StableRef/Copy` | Одиночное поле `StableRef<T>` | Копирует текущее значение во внутренний буфер. |
| `StableRef/Paste` | Одиночное поле `StableRef<T>` совместимого типа | Создаёт новую managed reference в целевом поле. |
| `Paste as New Element` | Элемент списка (в `StableRefList<T>` — любая часть строки: кнопка типа, ручка перетаскивания, отступы) | Вставляет скопированное значение новым элементом сразу после него. |
| `Duplicate Array Element` | Элемент списка | Вставляет полную копию сразу после элемента (стандартный Duplicate в Unity сделал бы оба элемента ссылками на один и тот же объект). |
| `Delete Array Element` | Элемент списка | Удаляет этот элемент. |
| `StableRef/Copy` | Заголовок `StableRefList<T>` (или массив `StableRef<T>`) | Копирует все элементы списка. |
| `StableRef/Paste/Replace` или `StableRef/Paste/Append` | Заголовок `StableRefList<T>` (или массив `StableRef<T>`) | Заменяет или добавляет скопированные элементы. |

Это безопасно между GameObject'ами, префабами и сценами: вместо копирования сырых сериализованных байт создаётся новая managed reference прямо в целевом документе, поэтому `managedReferences` не повреждается.

При копировании компонента со StableRef-полями между сценой и префабом используй это меню именно для StableRef-полей, а не стандартный Copy Component / Paste Component Values.

> **Предупреждение:** даже с этим меню под рукой сохраняй бдительность. Поля на основе `[SerializeReference]` (в том числе `StableRef`/`StableRefList`) не всегда очевидным образом копируются или переносятся даже при тривиальных встроенных операциях Unity — Duplicate, drag & drop в Hierarchy, Apply/Revert префаб-оверрайдов, слияние сцен/префабов и похожих действиях. Перед массовыми изменениями делай коммит или бэкап и проверяй результат после операции.

---

## Лицензия

Распространяется под [MIT License](LICENSE.md). Свободно для использования в личных и коммерческих проектах.

Автор — **Egor Shesterikov**.
