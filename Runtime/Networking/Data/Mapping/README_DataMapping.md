# DataMapping System

The DataMapping system provides a way to automatically map data fields from one format to another using a visual editor in Unity.

## Overview

The `DataMapping` class allows you to:
- Assign a `DataModel` that defines the source data structure
- Automatically generate mapping fields based on the DataModel
- Customize the target field names for data transformation
- Maintain a clean, controlled mapping configuration

## Components

### DataMapping
The main ScriptableObject that contains the mapping configuration.

### DataModel
Defines the structure of the source data with a collection of `DataField` objects.

### MappingField
Represents a single field mapping from source (`from`) to target (`to`).

## Usage

### 1. Create a DataModel
First, create a DataModel asset that defines your source data structure:

1. Right-click in the Project window
2. Select `Create > Molca > Networking > DataModel`
3. Add DataField entries with appropriate keys and types

### 2. Create a DataMapping
Create a DataMapping asset to define your field mappings:

1. Right-click in the Project window
2. Select `Create > Molca > Networking > DataMapping`
3. Assign your DataModel to the "Data Model" field

### 3. Customize Mappings
The custom editor will automatically:
- Generate mapping fields based on your DataModel
- Display the source field names (read-only)
- Allow you to customize the target field names

## Custom Editor Features

The DataMapping custom editor provides:

- **Automatic Field Generation**: Mapping fields are automatically created based on the assigned DataModel
- **Read-only Source Fields**: The "from" field names cannot be modified manually
- **Customizable Target Fields**: The "to" field names can be customized as needed
- **Real-time Updates**: Changes to the DataModel automatically update the mapping fields
- **Undo Support**: All changes support Unity's undo system
- **Validation**: Ensures the mapping configuration is always valid

## Example Use Cases

### API Response Mapping
Map API response fields to internal data model fields:
- Source: `{"user_id": 123, "full_name": "John Doe"}`
- Target: `{"id": 123, "name": "John Doe"}`

### Data Format Conversion
Convert between different data formats:
- Source: `{"first_name": "John", "last_name": "Doe"}`
- Target: `{"fullName": "John Doe"}`

### Field Renaming
Simply rename fields for consistency:
- Source: `{"userName": "john_doe"}`
- Target: `{"username": "john_doe"}`

## Technical Details

- The mapping fields list is controlled entirely by the editor
- Users cannot manually add or remove mapping fields
- Changes to the DataModel automatically regenerate the mapping fields
- The system preserves user customizations to the "to" field names when possible
- All operations support Unity's serialization and undo systems

## Best Practices

1. **Use Descriptive Names**: Choose clear, descriptive names for your target fields
2. **Maintain Consistency**: Use consistent naming conventions across your mappings
3. **Document Changes**: Use the target field names to document any data transformations
4. **Test Mappings**: Verify that your mappings work correctly with actual data
5. **Version Control**: Include your DataMapping assets in version control for team collaboration
