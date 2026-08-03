# Modal Types Summary

This document provides an overview of all available modal types in the Molca framework and their common use cases.

## Core Modal Types

### 1. ModalConfirmation
**Location**: `Assets/_Molca/_Core/Modals/ModalConfirmation.cs`

**Purpose**: Yes/No confirmation dialogs with customizable content.

**Features**:
- Regular and detailed confirmation styles
- Customizable title, subtitle, message, and details
- Configurable button texts
- Optional cancel button visibility

**Common Use Cases**:
- Delete confirmations
- Save changes prompts
- Exit game confirmations
- Purchase confirmations
- Data loss warnings

**Usage**:
```csharp
ModalManager.Instance.ShowRegularConfirmation(
    "Delete Item", 
    "Are you sure you want to delete this item?", 
    "Delete", 
    "Cancel", 
    () => DeleteItem(), 
    null
);
```

### 2. ModalMessage
**Location**: `Assets/_Molca/_Core/Modals/ModalMessage.cs`

**Purpose**: Temporary toast-style messages that auto-dismiss.

**Features**:
- Auto-fading messages
- Different message types (Default, Warning, Error)
- Color-coded by message type
- Configurable duration
- Object pooling for performance

**Common Use Cases**:
- Success notifications
- Error messages
- Warning alerts
- Status updates
- Debug information

**Usage**:
```csharp
ModalManager.Instance.AddMessage("Item saved successfully!", ModalManager.MessageType.Default, 3f);
ModalManager.Instance.AddMessage("Connection failed!", ModalManager.MessageType.Error, 5f);
```

### 3. ModalLoading
**Location**: `Assets/_Molca/_Core/Modals/ModalLoading.cs`

**Purpose**: Loading indicators with progress tracking.

**Features**:
- Progress bar with smooth animation
- Customizable title/message
- Progress percentage display
- Multiple loading instances support

**Common Use Cases**:
- File downloads
- Data processing
- Asset loading
- Network operations
- Save operations

**Usage**:
```csharp
var loading = ModalManager.Instance.AddLoading("Downloading...");
loading.Refresh("Downloading file 1 of 3", 0.33f);
// ... later
loading.Refresh("Downloading file 2 of 3", 0.66f);
ModalManager.Instance.RemoveLoading("Downloading...");
```

## SDK Modal Types

### 4. NumberInputKeyboard
**Location**: `Packages/com.molca.sdk/Runtime/Scripts/Modal/NumberInputKeyboard.cs`

**Purpose**: Custom number input with on-screen keyboard.

**Features**:
- 0-9 digit input
- Optional decimal support
- Configurable max digits
- Backspace and clear functionality
- Customizable button texts
- Input validation

**Common Use Cases**:
- PIN entry
- Age input
- Price/amount entry
- Quantity selection
- Score input
- Currency input

**Usage**:
```csharp
NumberInputKeyboardHelper.Show(
    title: "Enter PIN",
    initialValue: "",
    allowDecimals: false,
    maxDigits: 4,
    onConfirm: (result) => Debug.Log($"PIN: {result}")
);
```

### 5. TextInputModal
**Location**: `Packages/com.molca.sdk/Runtime/Scripts/Modal/TextInputModal.cs`

**Purpose**: Text input with validation and error handling.

**Features**:
- Single-line text input
- Character limit enforcement
- Custom validation functions
- Error message display
- Clear button functionality
- Optional empty input allowance

**Common Use Cases**:
- Username entry
- Email input
- Search queries
- Comments/notes
- Form fields
- Configuration values

**Usage**:
```csharp
TextInputModal.Show(
    title: "Enter Username",
    description: "Choose a unique username",
    placeholder: "Enter username...",
    maxLength: 20,
    allowEmpty: false,
    validation: (input) => input.Length >= 3,
    onConfirm: (result) => Debug.Log($"Username: {result}")
);
```

### 6. SelectionModal
**Location**: `Packages/com.molca.sdk/Runtime/Scripts/Modal/SelectionModal.cs`

**Purpose**: Single or multiple choice selection from a list of options.

**Features**:
- Single selection (radio buttons)
- Multiple selection (checkboxes)
- Option descriptions
- Disabled option support
- Select All/Deselect All (for multiple)
- Custom option data

**Common Use Cases**:
- Language selection
- Settings configuration
- Category selection
- Filter options
- Permission selection
- Theme selection

**Usage**:
```csharp
var options = new List<SelectionModal.SelectionOption>
{
    new SelectionModal.SelectionOption("en", "English", "Primary language"),
    new SelectionModal.SelectionOption("es", "Spanish", "Secondary language"),
    new SelectionModal.SelectionOption("fr", "French", "Optional language")
};

SelectionModal.ShowSingle(
    "Select Language",
    "Choose your preferred language",
    options,
    onConfirm: (option) => Debug.Log($"Selected: {option.displayText}")
);
```

### 7. ProgressModal
**Location**: `Packages/com.molca.sdk/Runtime/Scripts/Modal/ProgressModal.cs`

**Purpose**: Detailed progress tracking with pause/resume functionality.

**Features**:
- Progress bar with percentage
- Status message updates
- Pause/Resume functionality
- Cancel option
- Progress bar color customization
- Completion handling

**Common Use Cases**:
- File uploads/downloads
- Data synchronization
- Backup operations
- Import/export processes
- Long-running calculations
- Batch operations

**Usage**:
```csharp
var progressModal = ProgressModal.Show(
    "Uploading Files",
    "Preparing upload...",
    showCancelButton: true,
    showPauseButton: true,
    onCancel: () => Debug.Log("Upload cancelled"),
    onPause: () => Debug.Log("Upload paused")
);

// Update progress
progressModal.UpdateProgress(0.5f, "Uploading file 2 of 4");
progressModal.UpdateProgress(3, 4, "Uploading file 3 of 4");
progressModal.Complete("Upload finished!");
```

### 8. DatePickerModal
**Location**: `Packages/com.molca.sdk/Runtime/Scripts/Modal/DatePickerModal.cs`

**Purpose**: Calendar-based date selection with month navigation.

**Features**:
- Month/year navigation with arrow buttons
- Calendar grid with day selection
- Date range restrictions (min/max dates)
- Today button for quick selection
- Clear button to reset selection
- Custom validation support
- Visual styling for selected, today, and disabled dates
- Optional clear functionality

**Common Use Cases**:
- Birth date selection
- Appointment scheduling
- Event date selection
- Deadline setting
- Date range selection
- Form date inputs
- Calendar applications

**Usage**:
```csharp
DatePickerModal.Show(
    title: "Select Birth Date",
    description: "Choose your date of birth",
    initialDate: DateTime.Today.AddYears(-25),
    minDate: DateTime.Today.AddYears(-100),
    maxDate: DateTime.Today,
    allowClear: true,
    showTodayButton: true,
    onConfirm: (date) => Debug.Log($"Selected date: {date}"),
    onCancel: () => Debug.Log("Date selection cancelled")
);
```

### 9. DateField
**Location**: `Packages/com.molca.sdk/Runtime/Scripts/Modal/DateField.cs`

**Purpose**: Input field component for date selection with integrated date picker.

**Features**:
- Text display with placeholder
- Button to open date picker modal
- Clear button functionality
- Date range restrictions
- Custom date formatting
- Visual state management (normal, selected, error, disabled)
- Form validation support
- UnityEvent callbacks

**Common Use Cases**:
- Form date inputs
- Settings date fields
- Profile date fields
- Search date filters
- Date range inputs

**Usage**:
```csharp
// In inspector or code
dateField.SetDateRange(DateTime.Today.AddYears(-100), DateTime.Today);
dateField.SetDateFormat("MM/dd/yyyy");
dateField.SetRequired(true);
dateField.onDateChanged.AddListener((date) => Debug.Log($"Date changed: {date}"));
```

## Helper Components

### 8. ModalConfirmationHelper
**Location**: `Assets/_Molca/_Core/Modals/ModalConfirmationHelper.cs`

**Purpose**: Inspector-friendly confirmation dialog setup.

**Features**:
- UnityEvent-based callbacks
- Localization support
- Inspector configuration
- Regular and advanced modal support

### 9. NumberInputKeyboardHelper
**Location**: `Packages/com.molca.sdk/Runtime/Scripts/Modal/NumberInputKeyboardHelper.cs`

**Purpose**: Inspector-friendly number input setup.

**Features**:
- UnityEvent-based callbacks
- Localization support
- Inspector configuration
- Static methods for programmatic usage

### 10. DatePickerModalHelper
**Location**: `Packages/com.molca.sdk/Runtime/Scripts/Modal/DatePickerModalHelper.cs`

**Purpose**: Inspector-friendly date picker setup with predefined date ranges.

**Features**:
- UnityEvent-based callbacks
- Inspector configuration
- Predefined date range methods (future, past, current year, etc.)
- Custom date range support
- Static methods for programmatic usage

**Usage**:
```csharp
// In inspector or code
datePickerHelper.ShowFutureDatePicker();
datePickerHelper.ShowNext30DaysPicker();
datePickerHelper.ShowCurrentYearPicker();
datePickerHelper.ShowRangePicker(startDate, endDate);
```

## Modal Usage Patterns

### 1. Simple Confirmations
Use `ModalConfirmation` for basic yes/no decisions:
```csharp
ModalManager.Instance.ShowRegularConfirmation(
    "Save Changes?", 
    "Do you want to save your changes?", 
    "Save", 
    "Don't Save", 
    SaveChanges, 
    DiscardChanges
);
```

### 2. User Input
Use `TextInputModal` or `NumberInputKeyboard` for user input:
```csharp
TextInputModal.Show(
    "Enter Name",
    "Please enter your full name",
    placeholder: "John Doe",
    validation: (input) => input.Contains(" "),
    onConfirm: (name) => SavePlayerName(name)
);
```

### 3. Progress Tracking
Use `ProgressModal` for long-running operations:
```csharp
var progress = ProgressModal.Show("Processing Data");
for (int i = 0; i < items.Count; i++)
{
    if (progress.IsPaused) break;
    ProcessItem(items[i]);
    progress.UpdateProgress(i + 1, items.Count, $"Processing item {i + 1}");
}
progress.Complete("Processing complete!");
```

### 4. Selection Lists
Use `SelectionModal` for choice-based interactions:
```csharp
SelectionModal.ShowMultiple(
    "Select Categories",
    "Choose the categories you're interested in",
    categoryOptions,
    onConfirm: (selected) => ApplyFilters(selected)
);
```

### 5. Date Selection
Use `DatePickerModal` or `DateField` for date input:
```csharp
// Direct modal usage
DatePickerModal.Show(
    "Select Appointment Date",
    "Choose a date for your appointment",
    minDate: DateTime.Today,
    maxDate: DateTime.Today.AddDays(30),
    onConfirm: (date) => ScheduleAppointment(date.Value)
);

// DateField component usage
dateField.SetDateRange(DateTime.Today, DateTime.Today.AddDays(30));
dateField.onDateChanged.AddListener((date) => UpdateAppointmentDate(date));
```

## Best Practices

### 1. Modal Hierarchy
- Use appropriate modal types for specific use cases
- Avoid nesting modals unnecessarily
- Consider user flow and modal sequence

### 2. Error Handling
- Always provide cancel options for user-initiated operations
- Use appropriate message types for different scenarios
- Provide clear error messages with actionable information

### 3. Performance
- Use object pooling for frequently shown modals
- Clean up event listeners properly
- Avoid blocking operations in modal callbacks

### 4. User Experience
- Provide clear titles and descriptions
- Use consistent button text across the application
- Consider accessibility and localization
- Provide visual feedback for user actions

### 5. Validation
- Validate input before processing
- Provide immediate feedback for validation errors
- Use appropriate input types (number vs text vs selection)

## Integration with Localization

All modals support localization through the `DynamicLocalization` system:

```csharp
// In inspector or code
keyboardHelper.keyboardData.title.Init("NumberInput.Title");
keyboardHelper.keyboardData.confirmText.Init("NumberInput.Confirm");
```

## Future Modal Types to Consider

1. **ColorPickerModal** - Color selection with palette
2. **FilePickerModal** - File selection and upload
3. **RatingModal** - Star rating or feedback collection
4. **ShareModal** - Social media sharing options
5. **TutorialModal** - Step-by-step tutorial overlay
6. **NotificationModal** - Rich notification with actions
7. **SearchModal** - Advanced search with filters

These additional modal types would cover most common UI interaction patterns in modern applications. 