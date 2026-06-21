using UnityEngine;
using System;

namespace Molca.Attributes
{
    /// <summary>
    /// Displays an info box in the Unity Inspector with a message and optional icon.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    public class InfoBoxAttribute : PropertyAttribute
    {
        public string Message { get; private set; }
        public InfoBoxType Type { get; private set; }
        public bool IsMessageType { get; private set; }

        /// <summary>
        /// Creates an info box with a message and type.
        /// </summary>
        /// <param name="message">The message to display in the info box.</param>
        /// <param name="type">The type of info box (Info, Warning, or Error).</param>
        public InfoBoxAttribute(string message, InfoBoxType type = InfoBoxType.Info)
        {
            Message = message;
            Type = type;
            IsMessageType = true;
        }

        /// <summary>
        /// Creates an info box that displays a message based on a boolean field.
        /// </summary>
        /// <param name="message">The message to display when the condition is true.</param>
        /// <param name="type">The type of info box (Info, Warning, or Error).</param>
        public InfoBoxAttribute(string message, InfoBoxType type, bool isMessageType)
        {
            Message = message;
            Type = type;
            IsMessageType = isMessageType;
        }
    }

    /// <summary>
    /// The type of info box to display.
    /// </summary>
    public enum InfoBoxType
    {
        /// <summary>
        /// Displays an info message with a blue icon.
        /// </summary>
        Info,

        /// <summary>
        /// Displays a warning message with a yellow icon.
        /// </summary>
        Warning,

        /// <summary>
        /// Displays an error message with a red icon.
        /// </summary>
        Error
    }
} 