using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.DALC;

/// <summary>
/// Обработчик ошибок SQL — вызывается <see cref="DbManager"/> при возникновении <see cref="SqlException"/>.
/// Реализация регистрируется в DI и передаётся в <see cref="DbManager"/>.
/// </summary>
public interface ISqlErrorHandler
{
    /// <summary>
    /// Вызывается при ошибке выполнения SQL-запроса.
    /// </summary>
    /// <param name="exception">Исключение SQL Server.</param>
    /// <param name="connectionString">Строка подключения.</param>
    /// <param name="commandText">SQL-текст, отправленный на сервер (с параметрами-плейсхолдерами).</param>
    /// <param name="parameters">Параметры запроса (имя, значение).</param>
    void HandleSqlError(SqlException exception, string connectionString, string commandText, IReadOnlyList<(string Name, object? Value)> parameters);
}
