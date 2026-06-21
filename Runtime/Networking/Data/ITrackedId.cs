namespace Molca.Networking.Data
{
    /// <summary>
    /// Interface for classes that need unique ID tracking by DataIdGenerator
    /// </summary>
    public interface ITrackedId
    {
        /// <summary>
        /// The unique identifier for this tracked object
        /// </summary>
        string Id { get; set; }
        
        /// <summary>
        /// The type identifier for this tracked object (used for categorization)
        /// </summary>
        string TypeId { get; }
    }
}
