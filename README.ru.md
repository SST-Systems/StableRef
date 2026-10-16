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
- [Автоматическая генерация ID](#автоматическая-генерация-id)
  - [Типы, которые нельзя править: RefTypeIdFor](#типы-которые-нельзя-править-reftypeidfor)
- [Семплы](#семплы)
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

Стандартный `[SerializeReference]` хранит полное имя типа. Если переименовать или переместить класс, Unity теряет ссылку и поле становится `null`. `StableRef` хранит вместо этого постоянный ID типа, поэтому класс можно переименовывать свободно.

---

## Классы и атрибуты

| Тип | Назначение |
|---|---|
| `StableRef<T>` | Сериализуемая обёртка для одиночной полиморфной ссылки типа `T`. |
| `StableRefList<T>` | Сериализуемый список значений, переживающих переименования; работает как `IList<T>`. |
| `[RefTypeId("id")]` | Присваивает классу постоянный ID. Переименовывай класс как угодно — Unity его найдёт. |
| `[assembly: RefTypeIdFor(typeof(T), "id")]` | То же для типа, который нельзя править, — из другого пакета, DLL, сгенерированного кода. |
| `[RefCategory("Path")]` | Группирует тип под подменю в инспекторном селекторе. |
| `[RefSelector]` | Тот же селектор на обычном поле `[SerializeReference]`, без обёртки и без защиты от переименований. |

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

Значение `[RefTypeId]` должно быть уникальным в проекте — используй строки с неймспейсом. Атрибут **не наследуется**: классу-наследнику нужен свой `[RefTypeId]` (или свой файл скрипта, см. [Автоматическая генерация ID](#автоматическая-генерация-id)).

### StableRef\<T\> в поле

```csharp
public class ItemConfig : ScriptableObject
{
    public StableRef<IEffect> OnPickup;
}

if (config.OnPickup.HasValue)
    config.OnPickup.Reference.Apply();

config.OnPickup.Set(new DamageOnHit { Amount = 5 });
config.OnPickup = new StableRef<IEffect>(new DamageOnHit { Amount = 5 });
```

Лучше использовать `Set`, а не писать в `.Reference` напрямую: он обновляет сохранённый ID, когда меняется тип значения. Неявных преобразований между `StableRef<T>` и `T` нет — используй `.Reference` и конструктор.

### StableRefList\<T\>

```csharp
public class AbilityConfig : ScriptableObject
{
    public StableRefList<IEffect> Effects;
}

foreach (var effect in config.Effects)   // по значениям, без аллокаций
    effect?.Apply();

config.Effects.Add(new DamageOnHit { Amount = 5 });
config.Effects.RemoveAll(e => e is DamageOnHit);
var damage = config.Effects.OfType<DamageOnHit>().Sum(d => d.Amount);
config.Effects[0] = new Heal();
```

`StableRefList<T>` — это `IList<T>` / `IReadOnlyList<T>` самих значений: индексатор, `foreach`, LINQ и привычные методы `List<T>` работают с `T`. Сами записи доступны через `Items`.

Если список заполняется **из editor-скрипта**, вызови `StableRefResync.ResyncObject(asset)` перед сохранением (см. [Рефлексия и сериализаторы](#рефлексия-и-сериализаторы)).

<p align="center">
  <img src="Documentation~/inspector.gif" alt="Добавление типа через типизированный дропдаун" width="580">
</p>

### Копирование владельца: MemberwiseClone

Обёртки — классы, поэтому владелец, склонированный через `MemberwiseClone`, делит их с оригиналом: изменение значения в клоне меняет и оригинал. Дай клону собственные обёртки:

```csharp
public Effect ShallowClone()
{
    var clone = (Effect)MemberwiseClone();
    clone.Modifier = Modifier.ShallowCopy();   // новая обёртка, тот же экземпляр значения
    clone.Children = Children.ShallowCopy();   // новый список, те же экземпляры значений
    return clone;
}
```

### Рефлексия и сериализаторы

Код, который знает тип поля только во время выполнения (загрузчики конфигов, свои сериализаторы), может использовать необобщённый API:

```csharp
if (StableRefReflection.IsStableRef(field.FieldType, out var valueType))
{
    var entry = (StableRefBase)(field.GetValue(owner) ?? Activator.CreateInstance(field.FieldType));
    entry.BoxedValue = Convert(cell, valueType);
    field.SetValue(owner, entry);
}
else if (StableRefReflection.IsStableRefList(field.FieldType, out var elementType))
{
    var list = (StableRefListBase)(field.GetValue(owner) ?? Activator.CreateInstance(field.FieldType));
    list.SetBoxedValues(ConvertArray(cell, elementType));
    field.SetValue(owner, list);
}
```

У `StableRefBase` есть `BoxedValue`, `ValueBaseType`, `HasValue`; у `StableRefListBase` — `ElementType`, `Count`, `GetBoxed`, `SetBoxed`, `AddBoxed`, `SetBoxedValues`, `Clear`. Сеттеры проверяют тип и при несовпадении бросают `ArgumentException`.

**Запись в ассеты в редакторе.** Данные для восстановления записывает только инспектор. Инструмент, который пишет значения в ассеты (импортёр, генератор), должен вызывать `StableRefResync.ResyncObject(asset)` перед сохранением или запускать после себя **Tools → StableRef → Resync All** — иначе после переименования класса значение не восстановится корректно.

**Newtonsoft.Json:** готовый конвертер идёт семплом — [Newtonsoft.Json Converter](Samples~/NewtonsoftJson/README.ru.md).

### Обобщённые типы значений

Для поля вида `StableRefList<ICondition<Unit>>` селектор предлагает подходящие generic-типы, закрытые аргументом поля (`All<Unit>`, `Any<Unit>`). Каждому типу-аргументу нужен свой стабильный ID, как и любому другому типу StableRef.

Под кнопкой-шестерёнкой селектора — его настройки: размер, *Category in Label*, *Namespace Categories* и *Deep Search*. Namespace Categories (включено по умолчанию) кладёт тип без `[RefCategory]` в категорию его неймспейса (`Game.Effects` → *Game/Effects*); `[RefCategory]` всегда в приоритете. С Deep Search поиск совпадает ещё и с именем файла скрипта, поэтому `Conditions` находит все классы из `Conditions.cs`.

### Селектор без обёртки: [RefSelector]

Если нужен только селектор в инспекторе — без обёртки, без сохранённого ID, без лишних данных — пометь обычное поле `[SerializeReference]` атрибутом `[RefSelector]`:

```csharp
public class EffectAuthoring : MonoBehaviour
{
    [SerializeReference, RefSelector] private IEffect _onPickup;
    [SerializeReference, RefSelector] private List<IEffect> _effects;
}
```

Поле остаётся обычным `[SerializeReference]` (без `.Reference`), а атрибут не попадает в player-билд. Удобно для authoring-кода ECS, для существующих полей, которые не хочется мигрировать, и для больших списков, где метаданные на каждый элемент не окупаются. Перенести существующие поля вместе с данными помогает семпл [SerializeReference Migration](Samples~/SerializeReferenceMigration/README.ru.md).

> **На свой страх и риск.** У полей `[RefSelector]` **нет защиты от переименований**, и **Find Usages** / **Fix Missing Types** их не охватывают. Чтобы безопасно переименовать такой класс, добавь ему юнитевский `[MovedFrom]`.

---

## Автоматическая генерация ID

`[RefTypeId]` опционален. Без него StableRef использует как ID GUID файла скрипта (из его `.meta`):

- **Переименование класса** — безопасно.
- **Переименование или перемещение файла скрипта** — безопасно, GUID не меняется.
- **Удаление и повторное создание файла** — ссылка теряется; запись появится в Fix Missing Types.

Для типов, которые планируется активно рефакторить, явный `[RefTypeId]` надёжнее. Перевести существующий тип на `[RefTypeId]` безопасно: старые данные продолжают находиться и обновляются на новый ID при отрисовке в инспекторе или через **Tools → StableRef → Resync All**.

> **Важно:** один класс на файл скрипта — GUID принадлежит файлу, а не классу.

### Типы, которые нельзя править: RefTypeIdFor

На тип из другого пакета, DLL или source generator'а нельзя повесить `[RefTypeId]`. Назначь ему ID на уровне сборки — в любой сборке своего проекта:

```csharp
[assembly: RefTypeIdFor(typeof(ThirdParty.Effects.Burn), "my-game.third-party.burn")]
[assembly: RefTypeIdFor(typeof(ThirdParty.Conditions.All<>), "my-game.third-party.all")]
```

- **Приоритет:** `[RefTypeId]` на типе → `RefTypeIdFor` → GUID скрипта.
- **Generic:** назначай ID открытому определению (`All<>`).
- **Уникальность:** правила те же, что у `[RefTypeId]`; дубликаты логируются как ошибки.

---

## Семплы

Импортируются через **Window → Package Manager → StableRef → Samples**.

| Семпл | Что показывает |
|---|---|
| [Newtonsoft.Json Converter](Samples~/NewtonsoftJson/README.ru.md) | Поля StableRef читают и пишут тот же JSON, что и голые `T` / `List<T>`, — для ремоут-конфигов, сохранений и данных с сервера. Нужен Newtonsoft.Json. |
| [SerializeReference Migration](Samples~/SerializeReferenceMigration/README.ru.md) | Перевод проекта с полей `[SerializeReference]` на StableRef одной кнопкой — и кода, и сохранённых данных. |

---

## Инструменты редактора

Все инструменты доступны через **Tools → StableRef**. Они сканируют префабы и ScriptableObject в `Assets/` (включая под-ассеты) и открытые сейчас сцены.

Find Usages и Fix Missing Types показывают результаты деревом с поиском: клик по строке пингует объект, двойной клик или стрелки раскрывают, `Alt`+клик раскрывает всё внутри строки.

**Find Usages** — все места, где используется выбранный тип. Также доступно из контекстного меню скрипта: `Assets/Find StableRef Usages`.

<p align="center">
  <img src="Documentation~/find-usages.gif" alt="Окно Find Usages" width="640">
</p>

**Fix Missing Types** — находит StableRef-поля, чей тип больше не существует, чтобы починить сломанные ссылки после рефакторинга до того, как они превратятся в потерю данных.

<p align="center">
  <img src="Documentation~/fix-missing.gif" alt="Окно Fix Missing Types" width="560">
</p>

**Resync All** — приводит сохранённый ID и данные для восстановления каждой записи в соответствие с её текущим значением. Инспектор делает это сам, пока ты редактируешь; запускай Resync All после того, как значения записал код. Сами значения не меняются. Из кода: `StableRefResync.ResyncObject(target)` или `StableRefResync.ResyncAssets(paths)`.

**Metadata Size Report** — оценивает, сколько места занимают метаданные StableRef в билде и в ассетах.

**Метаданные в билде.** В player-билд попадают только ID типа и само значение. Отображаемое имя и снапшот для восстановления существуют только в редакторе и в билде ничего не стоят.

**Что восстанавливает recovery.** Каждая запись хранит снапшот своего значения. После переименования класса восстанавливаются его поля, вложенные объекты, списки и ссылки на `UnityEngine.Object`. `AnimationCurve` и `Gradient` не сохраняются.

Записи, которые не удалось разрешить, **пропускаются и сохраняются** — восстанови тип и запусти починку снова. Чтобы удалить такую запись, выбери None в её селекторе или **Set to None** в контекстном меню; замена сломанной записи всегда просит подтверждения.

**Когда Unity теряет ссылки, которые есть в файле**, починка возвращает их из файла, а не пересоздаёт:

- **Файл, который Unity не может прочитать.** Когда пропадает класс без сериализуемых полей, Unity сохраняет ссылку на него в виде, который потом сама не может загрузить, и объект при каждой загрузке теряет *все* свои ссылки. Починка удаляет из файла эту одну ссылку (поле становится None; запись с ID затем восстанавливается из снапшота) и пишет в лог, что удалено. Только для файлов в текстовом формате. Если класс переименован, перед починкой добавь ему `[MovedFrom]`. Ещё до сохранения скан и инспектор показывают такое поле как *Broken*, а Fix All (или кнопка-предупреждение у поля) ставит его в None, чтобы сохранение было безопасным, — запись сохраняет ID и восстановится, когда класс вернётся.
- **Ссылки, потерянные после domain reload.** Объект, который держит пропавший тип, на следующем domain reload может потерять в памяти все ссылки, хотя файл цел. Ассеты перечитываются с диска во время скана; открытые сцены перезагружаются после подтверждения — не сохраняй такую сцену до этого.

---

## Интеграция с инспектором

Для проектов со своим IMGUI-фреймворком инспектора:

- **`IStableRefChildrenDrawer`** — присвой его `StableRefDrawing.ChildrenDrawer`, чтобы поля внутри значений StableRef рисовались через твой фреймворк.
- **`IRefTypeMetadataProvider`** — имена, категории, подсказки и порядок типов в селекторе из твоих атрибутов.
- **`StableRefEditorUtility.GetValueProperty(property)`** — managed reference и для `StableRef<T>`, и для обычного поля `[SerializeReference]`.

Подробности — в XML-документации этих типов.

---

## Копирование и вставка

Встроенные в Unity **Copy Component** / **Paste Component Values** могут повредить данные `[SerializeReference]` при копировании между сценой и префабом. Пользуйся меню по правому клику:

- **Поле** — `StableRef/Copy`, `StableRef/Paste`.
- **Элемент списка** — `Paste as New Element`, `Duplicate Array Element` (полная копия), `Delete Array Element`.
- **Заголовок списка** — `StableRef/Copy` для всех элементов, `StableRef/Paste/Replace` или `StableRef/Paste/Append`.

> **Предупреждение:** поля `[SerializeReference]` (и StableRef тоже) не всегда переживают встроенные операции Unity — Duplicate, drag & drop, Apply/Revert префабов, слияния. Перед массовыми изменениями делай коммит.

---

## Лицензия

Распространяется под [MIT License](LICENSE.md). Свободно для использования в личных и коммерческих проектах.

Автор — **Egor Shesterikov**.
