using System.Data;
using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.DALC;

/// <summary>
/// Менеджер подключения к SQL Server через Dapper.
/// Управляет ленивым открытием <see cref="SqlConnection"/> и предоставляет методы для выполнения запросов.
/// Регистрируется как Scoped (одно подключение на HTTP-запрос).
/// При возникновении <see cref="SqlException"/> автоматически передаёт ошибку в <see cref="ISqlErrorHandler"/>.
/// </summary>
public class DbManager : IDisposable
{
    private readonly string _connectionString;
    private readonly ISqlErrorHandler? _errorHandler;
    private SqlConnection? _connection;

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
    /// Выполняет хранимую процедуру и возвращает коллекцию сущностей.
    /// </summary>
    public async Task<IEnumerable<T>> QueryStoredProcAsync<T>(string storedProcName, object? parameters = null, int? commandTimeout = null)
    {
        try
        {
            return await Connection.QueryAsync<T>(storedProcName, parameters, commandType: CommandType.StoredProcedure, commandTimeout: commandTimeout);
        }
        catch (SqlException ex)
        {
            _errorHandler?.HandleSqlError(ex, _connectionString, storedProcName, ExtractParams(parameters));
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
            return await Connection.QueryAsync<T>(sql, parameters, commandTimeout: commandTimeout);
        }
        catch (SqlException ex)
        {
            _errorHandler?.HandleSqlError(ex, _connectionString, sql, ExtractParams(parameters));
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
            return await Connection.ExecuteScalarAsync<T>(storedProcName, parameters, commandType: commandType);
        }
        catch (SqlException ex)
        {
            _errorHandler?.HandleSqlError(ex, _connectionString, storedProcName, ExtractParams(parameters));
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
            return await Connection.ExecuteAsync(storedProcName, parameters, commandType: commandType);
        }
        catch (SqlException ex)
        {
            _errorHandler?.HandleSqlError(ex, _connectionString, storedProcName, ExtractParams(parameters));
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
    }
}
