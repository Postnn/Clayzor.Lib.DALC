# Clayzor.Lib.DALC

Библиотека доступа к данным (Data Access Layer Component) решения **Clayzor**. Предоставляет `DbManager` — тонкую обёртку над [Dapper](https://github.com/DapperLib/Dapper) и `Microsoft.Data.SqlClient` для выполнения SQL-запросов, хранимых процедур и команд к SQL Server, с централизованной обработкой ошибок через `ISqlErrorHandler`.

> Базовый (leaf) проект решения: не зависит от других проектов Clayzor и подключается как `ProjectReference`. Отдельного NuGet-пакета и релизов нет.

## Содержание

- [Что это](#что-это)
- [Технологии и зависимости](#технологии-и-зависимости)
- [Состав](#состав)
- [Подключение](#подключение)
- [API DbManager](#api-dbmanager)
- [Примеры использования](#примеры-использования)
- [Обработка ошибок SQL](#обработка-ошибок-sql)
- [Паттерн доступа к данным](#паттерн-доступа-к-данным)
- [Пагинация для SQL Server 2008 R2](#пагинация-для-sql-server-2008-r2)
- [Важные правила и ограничения](#важные-правила-и-ограничения)
- [Разработка](#разработка)
- [Лицензия](#лицензия)

## Что это

`DbManager` управляет одним ленивым подключением `SqlConnection` в пределах своего DI-скоупа и выполняет через него запросы Dapper. Библиотека сознательно **не** содержит репозиториев, ORM и миграций: весь SQL хранится в виде именованных констант (в проекте `Clayzor.Lib.Entities`), а `DbManager` лишь исполняет их и единообразно перехватывает ошибки SQL Server.

Ключевая особенность — **сериализация доступа к подключению**. MARS (Multiple Active Result Sets) намеренно выключен, а все обращения к единственному `SqlConnection` проходят через шлюз `SemaphoreSlim`. Это защищает от `InvalidOperationException` в Blazor Server, где скоуп живёт столько же, сколько circuit, и рендерер может вклиниться с новым запросом посреди уже идущего.

## Технологии и зависимости

- **.NET 10** (`net10.0`), `Microsoft.NET.Sdk`, включены `ImplicitUsings` и `Nullable`.
- **Dapper** `2.*` — исполнение запросов и маппинг.
- **Microsoft.Data.SqlClient** `6.*` — драйвер SQL Server.
- Внешних `ProjectReference` нет.

## Состав

```
Clayzor.Lib.DALC/
├─ DbManager.cs           менеджер подключения и выполнения запросов (Scoped, IDisposable)
├─ ISqlErrorHandler.cs    контракт обработчика ошибок SQL
├─ AGENTS.md              правила доступа к данным для разработчиков/агентов
└─ Clayzor.Lib.DALC.csproj
```

## Подключение

`DbManager` регистрируется как **Scoped** — одно подключение на circuit в Blazor Server и на HTTP-запрос в ASP.NET Core. Конструктор принимает строку подключения и опциональный обработчик ошибок:

```csharp
public DbManager(string connectionString, ISqlErrorHandler? errorHandler = null)
```

Пример регистрации в `Program.cs` (детали зависят от приложения):

```csharp
// Реализация обработчика ошибок (например, из Clayzor.Lib.Web.Controls)
builder.Services.AddScoped<ISqlErrorHandler, ClayErrorService>();

builder.Services.AddScoped(sp => new DbManager(
    builder.Configuration.GetConnectionString("Default")!,
    sp.GetService<ISqlErrorHandler>()));
```

В компонентах и сервисах `DbManager` внедряется через DI:

```razor
@inject DbManager Db
```

При использовании `CommandType.Text` не забудьте `@using System.Data` в `_Imports.razor`.

## API DbManager

| Метод | Назначение |
| --- | --- |
| `QueryAsync<T>(sql, parameters?, commandTimeout?)` | Выборка raw SQL. По умолчанию `CommandType.Text`. Возвращает `IEnumerable<T>`. |
| `QueryStoredProcAsync<T>(name, parameters?, commandTimeout?)` | Выполнение хранимой процедуры с возвратом коллекции. |
| `ExecuteAsync(sql, parameters?, commandType?)` | Команды `INSERT`/`UPDATE`/`DELETE` или процедуры. **По умолчанию `CommandType.StoredProcedure`** — для raw SQL передавайте `commandType: CommandType.Text`. Возвращает число затронутых строк. |
| `ExecuteScalarAsync<T>(name, parameters?, commandType?)` | Скалярный результат. По умолчанию `CommandType.StoredProcedure`. |
| `RunAsync<T>(Func<SqlConnection, Task<T>>)` | Единственный законный способ работать с `SqlConnection` напрямую — под шлюзом. Не реентерабельный; результат должен быть буферизованным. |
| `Connection` | Ленивое подключение. Только для передачи в `RunAsync`; выполнять запросы напрямую через него нельзя. |
| `ConnectionString` | Текущая строка подключения. |
| `Dispose()` | Закрывает и освобождает подключение и шлюз. |

## Примеры использования

```csharp
// Выборка (raw SQL, CommandType.Text по умолчанию)
var items = await Db.QueryAsync<MyEntity>(SQLQueries.SELECT_МоиЗаписи);

// Команда изменения — ОБЯЗАТЕЛЬНО передать CommandType.Text
await Db.ExecuteAsync(SQLQueries.INSERT_МояТаблица, entity, commandType: CommandType.Text);

// Хранимая процедура
var rows = await Db.QueryStoredProcAsync<MyEntity>(SQLQueries.SP_МояПроцедура, new { Id = 5 });

// Скалярное значение из процедуры
var count = await Db.ExecuteScalarAsync<int>(SQLQueries.SP_Количество);

// Прямая работа с соединением под шлюзом
var value = await Db.RunAsync(conn =>
    conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM T", commandType: CommandType.Text));
```

## Обработка ошибок SQL

При `SqlException` `DbManager` вызывает зарегистрированный `ISqlErrorHandler` и **пробрасывает исключение дальше**:

```csharp
public interface ISqlErrorHandler
{
    void HandleSqlError(
        SqlException exception,
        string connectionString,
        string commandText,
        IReadOnlyList<(string Name, object? Value)> parameters);
}
```

`DbManager` сам извлекает параметры запроса (`ExtractParams`) — как из `DynamicParameters`, так и из обычных POCO-объектов — и передаёт их в обработчик вместе с текстом команды. Обработчик перехватывает ошибку **снаружи** шлюза `RunAsync`, поэтому обращаться из него в БД запрещено (иначе `HandleSqlError` выполнится под уже занятым шлюзом).

## Паттерн доступа к данным

Соглашения (подробнее в `AGENTS.md`):

- **Без репозиториев, ORM и миграций.** Весь SQL — именованные константы в `Clayzor.Lib.Entities/SQLQueries.cs`.
- **Именование констант:** `SELECT_{DataName}`, `INSERT_/UPDATE_/DELETE_{TableName}`, `SP_{Name}` (процедуры), `FN_{Name}` (функции).
- Весь доступ к БД идёт только через методы `DbManager` или `DbManager.RunAsync<T>` — прямые вызовы `SqlConnection`/Dapper в страницах и других сборках запрещены.
- **Имена колонок — русские** (`КодМедицинскогоАнализа`, `НазваниеАнализа` и т. п.). Свойства сущностей маппятся через `[Column(...)]` со ссылкой на константы из `ColumnNames.cs` (каждое имя определено ровно один раз).
- Каждый класс сущности регистрируется в `DapperColumnMapper.Initialize()`.

> Константы SQL, `ColumnNames.cs`, `DapperColumnMapper` и статические врапперы сущностей (`Entity.GetPagedAsync<T>` и др.) находятся в проекте `Clayzor.Lib.Entities`, а не здесь. Этот проект отвечает только за исполнение запросов.

## Пагинация для SQL Server 2008 R2

Целевая СУБД — SQL Server 2008 R2, поэтому `OFFSET/FETCH` (требует 2012+) **запрещён**. Пагинация строится на `ROW_NUMBER()`:

```sql
SELECT * FROM (
    SELECT _src.*, ROW_NUMBER() OVER (ORDER BY {orderBy}) AS _rn
    FROM ({selectSql}) _src
) _p WHERE _rn BETWEEN @__start AND @__end
```

Параметры: `@__start = (pageNumber - 1) * pageSize + 1`, `@__end = pageNumber * pageSize`. Реализация — в `Entity.GetPagedAsync<T>()` (проект `Clayzor.Lib.Entities`).

## Важные правила и ограничения

- **MARS выключен намеренно.** Доступ к единственному `SqlConnection` сериализуется шлюзом `SemaphoreSlim` внутри `RunAsync`.
- **`RunAsync` не реентерабельный** — внутри переданного действия нельзя вызывать другие методы `DbManager`.
- Результаты запросов должны быть **буферизованными** (у Dapper по умолчанию `buffered: true`) — незакрытый reader после выхода из-под шлюза приведёт к ошибке.
- Свойство `Connection` — только для передачи в `RunAsync`; напрямую выполнять через него запросы нельзя.
- `ISqlErrorHandler` не должен обращаться в БД.

## Разработка

`AGENTS.md` — основной ориентир для разработчиков и AI-агентов: он фиксирует паттерн доступа к данным, соглашения об именовании SQL-констант и колонок, а также правила потокобезопасности (`RunAsync`, отключённый MARS). Глобальные правила решения — в корневом `AGENTS.md` вышестоящего репозитория.

## Лицензия

Проект распространяется под лицензией **Apache License 2.0** — полный текст в файле [`LICENSE`](LICENSE) в корне репозитория.

Copyright © 2026 Bulychev Nick
