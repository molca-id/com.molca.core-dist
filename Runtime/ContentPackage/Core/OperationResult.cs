using System;

namespace Molca.ContentPackage.Core
{
    /// <summary>
    /// Represents the result of an operation with success/failure status and optional error information.
    /// </summary>
    [Serializable]
    public class OperationResult
    {
        /// <summary>
        /// Gets a value indicating whether the operation completed successfully.
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// Gets the error message if the operation failed, or null if successful.
        /// </summary>
        public string ErrorMessage { get; }

        /// <summary>
        /// Gets a value indicating whether the operation was cancelled by the user.
        /// </summary>
        public bool WasCancelled { get; }

        /// <summary>
        /// Initializes a new instance of the OperationResult class.
        /// </summary>
        /// <param name="success">Whether the operation was successful.</param>
        /// <param name="errorMessage">The error message if the operation failed.</param>
        /// <param name="wasCancelled">Whether the operation was cancelled.</param>
        protected OperationResult(bool success, string errorMessage = null, bool wasCancelled = false)
        {
            Success = success;
            ErrorMessage = errorMessage;
            WasCancelled = wasCancelled;
        }

        /// <summary>
        /// Creates a successful operation result.
        /// </summary>
        /// <returns>A successful OperationResult.</returns>
        public static OperationResult CreateSuccess()
        {
            return new OperationResult(true);
        }

        /// <summary>
        /// Creates a failed operation result with an error message.
        /// </summary>
        /// <param name="errorMessage">The error message describing why the operation failed.</param>
        /// <returns>A failed OperationResult with the specified error message.</returns>
        public static OperationResult CreateFailure(string errorMessage)
        {
            return new OperationResult(false, errorMessage);
        }

        /// <summary>
        /// Creates a cancelled operation result.
        /// </summary>
        /// <returns>A cancelled OperationResult.</returns>
        public static OperationResult CreateCancelled()
        {
            return new OperationResult(false, "Operation was cancelled", true);
        }
    }

    /// <summary>
    /// Represents the result of an operation that returns data, with success/failure status and optional error information.
    /// </summary>
    /// <typeparam name="T">The type of data returned by the operation.</typeparam>
    [Serializable]
    public class OperationResult<T> : OperationResult
    {
        /// <summary>
        /// Gets the data returned by the operation, or the default value if the operation failed.
        /// </summary>
        public T Data { get; }

        /// <summary>
        /// Initializes a new instance of the OperationResult&lt;T&gt; class.
        /// </summary>
        /// <param name="success">Whether the operation was successful.</param>
        /// <param name="data">The data returned by the operation.</param>
        /// <param name="errorMessage">The error message if the operation failed.</param>
        /// <param name="wasCancelled">Whether the operation was cancelled.</param>
        private OperationResult(bool success, T data = default, string errorMessage = null, bool wasCancelled = false)
            : base(success, errorMessage, wasCancelled)
        {
            Data = data;
        }

        /// <summary>
        /// Creates a successful operation result with data.
        /// </summary>
        /// <param name="data">The data returned by the successful operation.</param>
        /// <returns>A successful OperationResult&lt;T&gt; containing the specified data.</returns>
        public static OperationResult<T> CreateSuccess(T data)
        {
            return new OperationResult<T>(true, data);
        }

        /// <summary>
        /// Creates a failed operation result with an error message.
        /// </summary>
        /// <param name="errorMessage">The error message describing why the operation failed.</param>
        /// <returns>A failed OperationResult&lt;T&gt; with the specified error message.</returns>
        public static new OperationResult<T> CreateFailure(string errorMessage)
        {
            return new OperationResult<T>(false, default, errorMessage);
        }

        /// <summary>
        /// Creates a cancelled operation result.
        /// </summary>
        /// <returns>A cancelled OperationResult&lt;T&gt;.</returns>
        public static new OperationResult<T> CreateCancelled()
        {
            return new OperationResult<T>(false, default, "Operation was cancelled", true);
        }
    }
}