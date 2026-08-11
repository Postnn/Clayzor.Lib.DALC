> Глобальные правила и обзор решения — в корневом /AGENTS.md. Здесь — только специфика проекта Clayzor.Lib.DALC.

## Database access pattern

**No repository layer, no ORM, no migrations.** All SQL lives in `Clayzor.Lib.Entities/SQLQueries.cs` as named constants.

### SQL constant naming convention
- `SELECT_{DataName}` — запросы на выборку данных
- `INSERT_{TableName}` — добавление записей
- `UPDATE_{TableName}` — обновление записей
- `DELETE_{TableName}` — удаление записей
- `SP_{Name}` — хранимые процедуры
- `FN_{Name}` — пользовательские функции

### Rules
- Queries use `DbManager` (scoped: одно подключение на circuit в Blazor Server, на HTTP-запрос в ASP.NET Core) injected via `@inject DbManager Db`
- Raw SQL for queries: `Db.QueryAsync<T>(SQLQueries.CONST_NAME)` — `QueryAsync` defaults to `CommandType.Text`
- Raw SQL for commands: `Db.ExecuteAsync(SQLQueries.CONST_NAME, entity, commandType: CommandType.Text)` — **must pass `commandType: CommandType.Text`** because `ExecuteAsync` defaults to `CommandType.StoredProcedure`
- Stored procedures: `Db.QueryStoredProcAsync<T>(name, params)`
- All database access must go through `DbManager` methods or `DbManager.RunAsync<T>` — no direct `SqlConnection` or Dapper calls in pages or other assemblies
- **MARS выключен намеренно.** Доступ к единственному `SqlConnection` сериализован `SemaphoreSlim`-шлюзом (`RunAsync<T>`). Внешний код, работающий с `db.Connection` напрямую (минуя `RunAsync`), — запрещён
- **`ISqlErrorHandler` не имеет права обращаться в БД** — `catch(SqlException)` снаружи `RunAsync`, чтобы `HandleSqlError` не выполнялся под шлюзом
- **Контракт ошибок (CTFR3):** `RunAsync` вызывает `ISqlErrorHandler` ровно 1 раз для connectivity (label `"RunAsync"`, пустые params) и пробрасывает исключение. `ExecuteAsync` (write) всегда бросает `SqlException` — возвращённый 0 это валидный результат SQL, не сигнал ошибки. `ExecuteScalarAsync`/`QueryAsync`/`QueryStoredProcAsync` (read) возвращают default/пусто для connectivity. `DynamicSql.Query*` (read) возвращают default/пусто для connectivity. `OperationCanceledException` не перехватывается нигде.
- Column names in SQL are **Russian**: `КодМедицинскогоАнализа`, `НазваниеАнализа`, etc.
- Entity properties map to Russian columns via `[Column(MedA.Имя)]` referencing constants from `ColumnNames.cs` — каждое имя колонки определено ровно один раз
- Every entity class must be registered in `DapperColumnMapper.Initialize()`
- `@using System.Data` required in `_Imports.razor` when using `CommandType.Text`
- **Любая операция с базой данных обязана сопровождаться индикатором загрузки:**
  оверлей `.clay-busy` через `RunBusyAsync` (как в `ClayGrid.ExportMenu.cs`).
  После установки флага загрузки и вызова `StateHasChanged()` — `await Task.Delay(100)` для гарантированного
  рендера индикатора до начала операции. `Task.Yield()` не всегда успевает отдать рендер,
  особенно при вызове из дочернего компонента.

### SQL Server 2008 R2 pagination
- **Запрещено** использовать `OFFSET/FETCH` (требует SQL Server 2012+). Вместо этого — `ROW_NUMBER()`:
  ```sql
  SELECT * FROM (
      SELECT _src.*, ROW_NUMBER() OVER (ORDER BY {orderBy}) AS _rn
      FROM ({selectSql}) _src
  ) _p WHERE _rn BETWEEN @__start AND @__end
  ```
- Параметры: `@__start = (pageNumber - 1) * pageSize + 1`, `@__end = pageNumber * pageSize`
- Реализовано в `Entity.GetPagedAsync<T>()` — статические врапперы сущностей вызывают его через `Entity.GetPagedAsync<MyEntity>(...)`
