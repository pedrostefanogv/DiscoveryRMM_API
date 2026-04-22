using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// Tests for custom field conditions in automatic label rules.
/// Covers:
///   - AgentLabelExpressionValidator: custom field nodes (valid + invalid)
///   - AgentLabelExpressionValidator: operator compatibility per DataType
/// </summary>
public class AgentLabelCustomFieldTests
{
    private static readonly Guid DefinitionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // -------------------------------------------------------------------------
    // Validator: valid custom field nodes
    // -------------------------------------------------------------------------

    [Test]
    public void Validate_CustomFieldTextEquals_ReturnsNoErrors()
    {
        var expression = BuildCustomFieldCondition(
            AgentLabelField.AgentCustomField,
            DefinitionId,
            AgentLabelComparisonOperator.Equals,
            "SomeValue");

        var types = BuildTypes(CustomFieldDataType.Text);
        var errors = AgentLabelExpressionValidator.Validate(expression, types);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Validate_CustomFieldIntegerGreaterThan_ReturnsNoErrors()
    {
        var expression = BuildCustomFieldCondition(
            AgentLabelField.AgentCustomField,
            DefinitionId,
            AgentLabelComparisonOperator.GreaterThan,
            "5");

        var types = BuildTypes(CustomFieldDataType.Integer);
        var errors = AgentLabelExpressionValidator.Validate(expression, types);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Validate_CustomFieldBooleanEquals_ReturnsNoErrors()
    {
        var expression = BuildCustomFieldCondition(
            AgentLabelField.AgentCustomField,
            DefinitionId,
            AgentLabelComparisonOperator.Equals,
            "true");

        var types = BuildTypes(CustomFieldDataType.Boolean);
        var errors = AgentLabelExpressionValidator.Validate(expression, types);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Validate_ClientCustomFieldScopeEquals_ReturnsNoErrors()
    {
        var expression = BuildCustomFieldCondition(
            AgentLabelField.ClientCustomField,
            DefinitionId,
            AgentLabelComparisonOperator.Equals,
            "Gold");

        var types = BuildTypes(CustomFieldDataType.Dropdown);
        var errors = AgentLabelExpressionValidator.Validate(expression, types);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Validate_SiteCustomFieldContains_ReturnsNoErrors()
    {
        var expression = BuildCustomFieldCondition(
            AgentLabelField.SiteCustomField,
            DefinitionId,
            AgentLabelComparisonOperator.Contains,
            "prod");

        var types = BuildTypes(CustomFieldDataType.Text);
        var errors = AgentLabelExpressionValidator.Validate(expression, types);

        Assert.That(errors, Is.Empty);
    }

    // -------------------------------------------------------------------------
    // Validator: missing CustomFieldDefinitionId
    // -------------------------------------------------------------------------

    [Test]
    public void Validate_AgentCustomField_MissingDefinitionId_ReturnsError()
    {
        var expression = new AgentLabelRuleExpressionNodeDto
        {
            NodeType = AgentLabelNodeType.Condition,
            Field = AgentLabelField.AgentCustomField,
            CustomFieldDefinitionId = null,
            Operator = AgentLabelComparisonOperator.Equals,
            Value = "test"
        };

        var types = BuildTypes(CustomFieldDataType.Text);
        var errors = AgentLabelExpressionValidator.Validate(expression, types);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("CustomFieldDefinitionId"));
    }

    // -------------------------------------------------------------------------
    // Validator: definition not found in provided types
    // -------------------------------------------------------------------------

    [Test]
    public void Validate_AgentCustomField_UnknownDefinitionId_ReturnsError()
    {
        var expression = BuildCustomFieldCondition(
            AgentLabelField.AgentCustomField,
            Guid.NewGuid(), // not in types
            AgentLabelComparisonOperator.Equals,
            "test");

        var types = BuildTypes(CustomFieldDataType.Text);
        var errors = AgentLabelExpressionValidator.Validate(expression, types);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("not found"));
    }

    // -------------------------------------------------------------------------
    // Validator: operator incompatibility per DataType
    // -------------------------------------------------------------------------

    [Test]
    public void Validate_BooleanField_TextOperator_ReturnsError()
    {
        var expression = BuildCustomFieldCondition(
            AgentLabelField.AgentCustomField,
            DefinitionId,
            AgentLabelComparisonOperator.Contains, // not allowed for Boolean
            "true");

        var types = BuildTypes(CustomFieldDataType.Boolean);
        var errors = AgentLabelExpressionValidator.Validate(expression, types);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("operator"));
    }

    [Test]
    public void Validate_IntegerField_ContainsOperator_ReturnsError()
    {
        var expression = BuildCustomFieldCondition(
            AgentLabelField.AgentCustomField,
            DefinitionId,
            AgentLabelComparisonOperator.Contains, // not allowed for Integer
            "5");

        var types = BuildTypes(CustomFieldDataType.Integer);
        var errors = AgentLabelExpressionValidator.Validate(expression, types);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("operator"));
    }

    [Test]
    public void Validate_TextFieldDropdown_GreaterThan_ReturnsError()
    {
        var expression = BuildCustomFieldCondition(
            AgentLabelField.AgentCustomField,
            DefinitionId,
            AgentLabelComparisonOperator.GreaterThan, // not allowed for Dropdown
            "Gold");

        var types = BuildTypes(CustomFieldDataType.Dropdown);
        var errors = AgentLabelExpressionValidator.Validate(expression, types);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("operator"));
    }

    // -------------------------------------------------------------------------
    // Validator: no customFieldTypes passed — nodes with no definition validation
    // -------------------------------------------------------------------------

    [Test]
    public void Validate_CustomField_NoTypesProvided_ReturnsDefinitionNotFoundError()
    {
        var expression = BuildCustomFieldCondition(
            AgentLabelField.AgentCustomField,
            DefinitionId,
            AgentLabelComparisonOperator.Equals,
            "val");

        // Passing null — same as not providing types
        var errors = AgentLabelExpressionValidator.Validate(expression, null);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("not found"));
    }

    // -------------------------------------------------------------------------
    // Validator: valid regex on text custom field
    // -------------------------------------------------------------------------

    [Test]
    public void Validate_CustomFieldRegex_ValidPattern_ReturnsNoErrors()
    {
        var expression = BuildCustomFieldCondition(
            AgentLabelField.AgentCustomField,
            DefinitionId,
            AgentLabelComparisonOperator.Regex,
            @"^\d{4}$");

        var types = BuildTypes(CustomFieldDataType.Text);
        var errors = AgentLabelExpressionValidator.Validate(expression, types);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Validate_CustomFieldRegex_InvalidPattern_ReturnsError()
    {
        var expression = BuildCustomFieldCondition(
            AgentLabelField.AgentCustomField,
            DefinitionId,
            AgentLabelComparisonOperator.Regex,
            "(unclosed");

        var types = BuildTypes(CustomFieldDataType.Text);
        var errors = AgentLabelExpressionValidator.Validate(expression, types);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("regex"));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AgentLabelRuleExpressionNodeDto BuildCustomFieldCondition(
        AgentLabelField field,
        Guid definitionId,
        AgentLabelComparisonOperator op,
        string value)
        => new()
        {
            NodeType = AgentLabelNodeType.Condition,
            Field = field,
            CustomFieldDefinitionId = definitionId,
            Operator = op,
            Value = value
        };

    private static IReadOnlyDictionary<Guid, CustomFieldDataType> BuildTypes(CustomFieldDataType dataType)
        => new Dictionary<Guid, CustomFieldDataType> { [DefinitionId] = dataType };
}
