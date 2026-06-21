using System;

namespace Molca.Networking.Data
{
    public enum DataType
    {
        String,
        Int,
        Float,
        Bool,
        Model
    }

    /// <summary>
    /// Represents a single field definition within a data model.
    /// Defines the structure, type, and validation rules for a data field.
    /// </summary>
    [Serializable]
    public class DataField
    {
        /// <summary>
        /// Unique identifier for this field within the data model.
        /// </summary>
        public string key;
        
        /// <summary>
        /// The data type of this field.
        /// </summary>
        public DataType type;
        
        /// <summary>
        /// Reference to the nested data model if this field is of type Model.
        /// </summary>
        public DataModel model;
        
        /// <summary>
        /// Whether this field represents an array of the specified type.
        /// </summary>
        public bool isArray;

        /// <summary>
        /// Default constructor.
        /// </summary>
        public DataField()
        {
            key = string.Empty;
            type = DataType.String;
            model = null;
            isArray = false;
        }

        /// <summary>
        /// Constructor with key and type.
        /// </summary>
        /// <param name="key">The field identifier</param>
        /// <param name="type">The data type</param>
        public DataField(string key, DataType type)
        {
            this.key = key;
            this.type = type;
            this.model = null;
            this.isArray = false;
        }

        /// <summary>
        /// Constructor with all properties.
        /// </summary>
        /// <param name="key">The field identifier</param>
        /// <param name="type">The data type</param>
        /// <param name="model">The nested model reference</param>
        /// <param name="isArray">Whether this is an array</param>
        public DataField(string key, DataType type, DataModel model, bool isArray)
        {
            this.key = key;
            this.type = type;
            this.model = model;
            this.isArray = isArray;
        }

        /// <summary>
        /// Validates that this field has a valid configuration.
        /// </summary>
        /// <returns>True if the field is valid, false otherwise</returns>
        public bool IsValid()
        {
            // Key must not be empty
            if (string.IsNullOrEmpty(key))
                return false;

            // If type is Model, model reference must be set
            if (type == DataType.Model && model == null)
                return false;

            return true;
        }

        /// <summary>
        /// Gets a display name for this field.
        /// </summary>
        /// <returns>The display name</returns>
        public string GetDisplayName()
        {
            var displayName = key;
            
            if (isArray)
                displayName += "[]";
                
            if (type == DataType.Model && model != null)
                displayName += $" ({model.name})";
                
            return displayName;
        }
    }
}