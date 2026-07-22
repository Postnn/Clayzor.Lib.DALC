using System.Data;
using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.DALC;

/// <summary>
/// Менеджер подключения к SQL Server через Dapper.
/// Управляет ленивым открытием <see cref="SqlConnection"/> и предоставляет методы для выполнения запросов.
/// Регистрируется как Scoped (одно подключение на circuit в Blazor Server, на HTTP-запрос в ASP.NET Core).
/// При возникновении <see cref="SqlException"/> автоматически передаёт ошибку в <see cref="ISqlErrorHandler"/>.
/// </summary>
public class DbManager : IDisposable
{
    private readonly string _connectionString;
    private readonly ISqlErrorHandler? _errorHandler;
    private SqlConnection? _connection;

    /// <summary>
    /// Сериализует доступ к единственному соединению скоупа. В Blazor Server скоуп живёт
    /// столько же, сколько circuit, а рендерер может вызвать OnAfterRenderAsync посреди
    /// уже запущенного обработчика — два await'а на одном SqlConnection без MARS дают
    /// InvalidOperationException.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Создаёт экземпляр <see cref="DbManager"/> с указанной строкой подключения.
    /// </summary>
    /// <param name="connectionString">Строка подключения к SQL Server.</param>
    /// <param name="errorHandler">Обработчик ошибок SQL (опционально).</param>
    public DbManager(string connectionString, ISqlErrorHandler? errorHandler = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _errorHandler = errorHandler;
    }

    /// <summary>
    /// Строка подключения к SQL Server.
    /// </summary>
    public string ConnectionString => _connectionString;

    /// <summary>
    /// Ленивое подключение к SQL Server. Открывается при первом обращении, повторно используется в рамках скоупа.
    /// ВНИМАНИЕ: выполнять запросы напрямую через это свойство НЕЛЬЗЯ — только через методы
    /// <see cref="DbManager"/> или <see cref="RunAsync{T}"/>.
    /// </summary>
    public SqlConnection Connection
    {
        get
        {
            if (_connection is null)
                _connection = new SqlConnection(_connectionString);
            if (_connection.State != ConnectionState.Open)
                _connection.Open();
            return _connection;
        }
    }

    /// <summary>
    /// Выполняет операцию на соединении скоупа под шлюзом. Единственный законный способ
    /// работать с <see cref="SqlConnection"/> снаружи DbManager.
    /// Внутри действия нельзя вызывать другие методы DbManager — шлюз не реентерабельный.
    /// Результат обязан быть буферизованным (Dapper по умолчанию buffered: true):
    /// незакрытый reader после выхода из-под шлюза вернёт ошибку.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<SqlConnection, Task<T>> action)
    {
        await _gate.WaitAsync();
        try
        {
            return await action(Connection);
        }
        catch (SqlException ex) when (IsConnectivityError(ex))
        {
            _errorHandler?.HandleSqlError(ex, _connectionString, "RunAsync", []);
            return default!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Выполняет хранимую процедуру и возвращает коллекцию сущностей.
    /// </summary>
    public async Task<IEnumerable<T>> QueryStoredProcAsync<T>(string storedProcName, object? parameters = null, int? commandTimeout = null)
    {
        try
        {
            return await RunAsync(c => c.QueryAsync<T>(storedProcName, parameters, commandType: CommandType.StoredProcedure, commandTimeout: commandTimeout));
        }
        catch (SqlException ex)
        {
            _errorHandler?.HandleSqlError(ex, _connectionString, storedProcName, ExtractParams(parameters));
            if (IsConnectivityError(ex))
                return [];
            throw;
        }
    }

    /// <summary>
    /// Выполняет raw SQL-запрос на выборку и возвращает коллекцию сущностей.
    /// </summary>
    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null, int? commandTimeout = null)
    {
        try
        {
            return await RunAsync(c => c.QueryAsync<T>(sql, parameters, commandTimeout: commandTimeout));
        }
        catch (SqlException ex)
        {
            _errorHandler?.HandleSqlError(ex, _connectionString, sql, ExtractParams(parameters));
            if (IsConnectivityError(ex))
                return [];
            throw;
        }
    }

    /// <summary>
    /// Выполняет хранимую процедуру и возвращает скалярное значение.
    /// </summary>
    public async Task<T?> ExecuteScalarAsync<T>(string storedProcName, object? parameters = null, CommandType commandType = CommandType.StoredProcedure)
    {
        try
        {
            return await RunAsync(c => c.ExecuteScalarAsync<T>(storedProcName, parameters, commandType: commandType));
        }
        catch (SqlException ex)
        {
            _errorHandler?.HandleSqlError(ex, _connectionString, storedProcName, ExtractParams(parameters));
            if (IsConnectivityError(ex))
                return default;
            throw;
        }
    }

    /// <summary>
    /// Выполняет команду (INSERT, UPDATE, DELETE) или хранимую процедуру.
    /// </summary>
    public async Task<int> ExecuteAsync(string storedProcName, object? parameters = null, CommandType commandType = CommandType.StoredProcedure)
    {
        try
        {
            return await RunAsync(c => c.ExecuteAsync(storedProcName, parameters, commandType: commandType));
        }
        catch (SqlException ex)
        {
            _errorHandler?.HandleSqlError(ex, _connectionString, storedProcName, ExtractParams(parameters));
            if (IsConnectivityError(ex))
                return default;
            throw;
        }
    }

    /// <summary>
    /// Извлекает список параметров (имя, значение) из объекта параметров Dapper.
    /// </summary>
    private static IReadOnlyList<(string Name, object? Value)> ExtractParams(object? parameters)
    {
        if (parameters is null)
            return [];

        if (parameters is DynamicParameters dp)
        {
            var list = new List<(string, object?)>();
            foreach (var name in dp.ParameterNames)
            {
                var value = dp.Get<object?>(name);
                list.Add((name, value));
            }
            return list;
        }

        return parameters.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (p.Name, p.GetValue(parameters)))
            .ToList();
    }

    /// <summary>
    /// Коды ошибок SQL Server, связанные с сетевыми проблемами и недоступностью сервера.
    /// </summary>
    private static readonly HashSet<int> ConnectivityErrorCodes = new()
    {
        2, 40, 53, 64, 121, 233, 258, 4060, 11001, 1231, -1, -2,
    };

    /// <summary>
    /// Определяет, является ли код ошибки SQL Server ошибкой недоступности сервера.
    /// </summary>
    /// <param name="errorNumber">Код ошибки (<see cref="SqlException.Number"/>).</param>
    /// <returns><c>true</c>, если код связан с недоступностью сервера.</returns>
    internal static bool IsConnectivityErrorCode(int errorNumber)
    {
        return ConnectivityErrorCodes.Contains(errorNumber);
    }

    /// <summary>
    /// Определяет, является ли исключение SQL Server ошибкой недоступности сервера
    /// (потеря сетевого соединения, сервер не найден, таймаут подключения).
    /// Такие ошибки не должны ронять страницу — вместо этого показывается оверлей
    /// с автоматическим переподключением.
    /// </summary>
    /// <param name="ex">Исключение SQL Server.</param>
    /// <returns><c>true</c>, если ошибка связана с недоступностью сервера.</returns>
    /// <remarks>
    /// Коды ошибок SQL Server, классифицируемые как connectivity-проблемы:
    /// <list type="bullet">
    ///   <item><b>2</b> — Timeout expired (connection timeout).</item>
    ///   <item><b>40</b> — Could not open a connection to SQL Server (Named Pipes).</item>
    ///   <item><b>53</b> — Named Pipes provider: could not open a connection.</item>
    ///   <item><b>121</b> — Semaphore timeout (resource pool exhaustion).</item>
    ///   <item><b>233</b> — Client was unable to establish a connection (pipe closed).</item>
    ///   <item><b>258</b> — Login timeout expired.</item>
    ///   <item><b>4060</b> — Cannot open database requested by login.</item>
    ///   <item><b>11001</b> — Host not found (Winsock).</item>
    ///   <item><b>1231</b> — Network-related or instance-specific error.</item>
    ///   <item><b>-1</b> — Transport-level error.</item>
    ///   <item><b>-2</b> — DBNETLIB Connection timeout / ATTN timeout.</item>
    /// </list>
    /// Также проверяет <see cref="System.ComponentModel.Win32Exception"/> во
    /// <see cref="Exception.InnerException"/> (например, ошибка 2 «Не удаётся найти указанный файл»).
    /// </remarks>
    public static bool IsConnectivityError(SqlException ex)
    {
        if (ConnectivityErrorCodes.Contains(ex.Number))
            return true;

        // Win32Exception внутри SqlException (например «Не удаётся найти указанный файл»)
        // указывает на проблему на уровне транспорта / сокета
        if (ex.InnerException is System.ComponentModel.Win32Exception win32
            && ConnectivityErrorCodes.Contains(win32.NativeErrorCode))
            return true;

        // Fallback: сообщения о разрыве/невозможности восстановления подключения
        // (драйвер Microsoft.Data.SqlClient может вернуть нестандартный код)
        if (ex.Message.Contains("разрыв подключения", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("восстановление невозможно", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Закрывает и освобождает подключение.
    /// </summary>
    public void Dispose()
    {
        if (_connection is not null)
        {
            if (_connection.State != ConnectionState.Closed)
                _connection.Close();
            _connection.Dispose();
            _connection = null;
        }
        _gate.Dispose();
    }
}
