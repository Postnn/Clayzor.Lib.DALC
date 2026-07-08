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
- Queries use `DbManager` (scoped per-request) injected via `@inject DbManager Db`
- Raw SQL for queries: `Db.QueryAsync<T>(SQLQueries.CONST_NAME)` — `QueryAsync` defaults to `CommandType.Text`
- Raw SQL for commands: `Db.ExecuteAsync(SQLQueries.CONST_NAME, entity, commandType: CommandType.Text)` — **must pass `commandType: CommandType.Text`** because `ExecuteAsync` defaults to `CommandType.StoredProcedure`
- Stored procedures: `Db.QueryStoredProcAsync<T>(name, params)`
- All database access must go through `DbManager` methods — no direct `SqlConnection` or Dapper calls in pages or other assemblies
- Column names in SQL are **Russian**: `КодМедицинскогоАнализа`, `НазваниеАнализа`, etc.
- Entity properties map to Russian columns via `[Column(MedA.Имя)]` referencing constants from `ColumnNames.cs` — каждое имя колонки определено ровно один раз
- Every entity class must be registered in `DapperColumnMapper.Initialize()`
- `@using System.Data` required in `_Imports.razor` when using `CommandType.Text`

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
