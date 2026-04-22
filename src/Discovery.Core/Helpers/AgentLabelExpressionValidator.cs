using System.Globalization;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Helpers;

/// <summary>
/// Validates an <see cref="AgentLabelRuleExpressionNodeDto"/> tree.
/// Pass <paramref name="customFieldTypes"/> (DefinitionId → DataType) to enable validation of
/// <see cref="AgentLabelField.AgentCustomField" />, <see cref="AgentLabelField.ClientCustomField" /> and
/// <see cref="AgentLabelField.SiteCustomField" /> nodes.
/// </summary>
public static class AgentLabelExpressionValidator
{
    public const int MaxDepth = 8;
    public const int MaxNodes = 128;
    public const int MaxChildrenPerGroup = 20;
    public const int MaxValueLength = 512;
    public const int MaxRegexLength = 256;

    private static readonly HashSet<AgentLabelField> TextFields =
    [
        AgentLabelField.Hostname,
        AgentLabelField.DisplayName,
        AgentLabelField.IpAddress,
        AgentLabelField.OperatingSystem,
        AgentLabelField.OsVersion,
        AgentLabelField.SoftwareName,
        AgentLabelField.SoftwarePublisher,
        AgentLabelField.SoftwareVersion,
        AgentLabelField.Processor
    ];

    private static readonly HashSet<AgentLabelField> NumericFields =
    [
        AgentLabelField.SoftwareCount,
        AgentLabelField.TotalMemoryBytes,
        AgentLabelField.TotalDisksCount
    ];

    private static readonly HashSet<AgentLabelField> CustomFields =
    [
        AgentLabelField.AgentCustomField,
        AgentLabelField.ClientCustomField,
        AgentLabelField.SiteCustomField
    ];

    private static readonly HashSet<AgentLabelComparisonOperator> TextOperators =
    [
        AgentLabelComparisonOperator.Contains,
        AgentLabelComparisonOperator.NotContains,
        AgentLabelComparisonOperator.StartsWith,
        AgentLabelComparisonOperator.EndsWith,
        AgentLabelComparisonOperator.Equals,
        AgentLabelComparisonOperator.NotEquals,
        AgentLabelComparisonOperator.Regex
    ];

    private static readonly HashSet<AgentLabelComparisonOperator> NumericOperators =
    [
        AgentLabelComparisonOperator.Equals,
        AgentLabelComparisonOperator.NotEquals,
        AgentLabelComparisonOperator.GreaterThan,
        AgentLabelComparisonOperator.GreaterThanOrEqual,
        AgentLabelComparisonOperator.LessThan,
        AgentLabelComparisonOperator.LessThanOrEqual
    ];

    private static readonly HashSet<AgentLabelComparisonOperator> StatusOperators =
    [
        AgentLabelComparisonOperator.Equals,
        AgentLabelComparisonOperator.NotEquals
    ];

    public static IReadOnlyList<string> Validate(
        AgentLabelRuleExpressionNodeDto expression,
        IReadOnlyDictionary<Guid, CustomFieldDataType>? customFieldTypes = null)
    {
        var errors = new List<string>();
        var nodeCount = 0;
        ValidateNode(expression, 1, ref nodeCount, errors, "root", customFieldTypes);
        return errors;
    }

    private static void ValidateNode(
        AgentLabelRuleExpressionNodeDto node,
        int depth,
        ref int nodeCount,
        List<string> errors,
        string path,
        IReadOnlyDictionary<Guid, CustomFieldDataType>? customFieldTypes)
    {
        nodeCount++;

        if (depth > MaxDepth)
            errors.Add($"{path}: expression depth exceeds maximum of {MaxDepth}.");

        if (nodeCount > MaxNodes)
            errors.Add($"expression node count exceeds maximum of {MaxNodes}.");

        if (node.NodeType == AgentLabelNodeType.Group)
        {
            if (!node.LogicalOperator.HasValue)
                errors.Add($"{path}: group node requires LogicalOperator.");

            if (node.Field.HasValue || node.Operator.HasValue || node.Value is not null)
                errors.Add($"{path}: group node cannot define Field/Operator/Value.");

            if (node.Children.Count == 0)
                errors.Add($"{path}: group node must have at least one child.");

            if (node.Children.Count > MaxChildrenPerGroup)
                errors.Add($"{path}: group node exceeds maximum of {MaxChildrenPerGroup} children.");

            for (var i = 0; i < node.Children.Count; i++)
            {
                ValidateNode(node.Children[i], depth + 1, ref nodeCount, errors, $"{path}.children[{i}]", customFieldTypes);
            }

            return;
        }

        if (node.NodeType != AgentLabelNodeType.Condition)
        {
            errors.Add($"{path}: invalid NodeType.");
            return;
        }

        if (node.Children.Count > 0)
            errors.Add($"{path}: condition node cannot contain children.");

        if (node.LogicalOperator.HasValue)
            errors.Add($"{path}: condition node cannot define LogicalOperator.");

        if (!node.Field.HasValue)
            errors.Add($"{path}: condition node requires Field.");

        if (!node.Operator.HasValue)
            errors.Add($"{path}: condition node requires Operator.");

        if (node.Value is null)
            errors.Add($"{path}: condition node requires Value.");

        if (!node.Field.HasValue || !node.Operator.HasValue || node.Value is null)
            return;

        ValidateCondition(node.Field.Value, node.CustomFieldDefinitionId, node.Operator.Value, node.Value, errors, path, customFieldTypes);
    }

    private static void ValidateCondition(
        AgentLabelField field,
        Guid? customFieldDefinitionId,
        AgentLabelComparisonOperator op,
        string value,
        List<string> errors,
        string path,
        IReadOnlyDictionary<Guid, CustomFieldDataType>? customFieldTypes)
    {
        if (value.Length > MaxValueLength)
            errors.Add($"{path}: value exceeds maximum length of {MaxValueLength}.");

        if (field == AgentLabelField.Status)
        {
            if (!StatusOperators.Contains(op))
                errors.Add($"{path}: operator '{op}' is not allowed for field '{field}'.");

            if (!Enum.TryParse<AgentStatus>(value, true, out _))
                errors.Add($"{path}: value '{value}' is not a valid AgentStatus.");

            return;
        }

        if (TextFields.Contains(field))
        {
            if (!TextOperators.Contains(op))
                errors.Add($"{path}: operator '{op}' is not allowed for text field '{field}'.");

            if (op == AgentLabelComparisonOperator.Regex && value.Length > MaxRegexLength)
                errors.Add($"{path}: regex pattern exceeds maximum length of {MaxRegexLength}.");

            if (op == AgentLabelComparisonOperator.Regex)
            {
                try
                {
                    _ = new System.Text.RegularExpressions.Regex(value);
                }
                catch (ArgumentException)
                {
                    errors.Add($"{path}: invalid regex pattern.");
                }
            }

            return;
        }

        if (NumericFields.Contains(field))
        {
            if (!NumericOperators.Contains(op))
                errors.Add($"{path}: operator '{op}' is not allowed for numeric field '{field}'.");

            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
                && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out _))
            {
                errors.Add($"{path}: value '{value}' is not a valid number.");
            }

            return;
        }

        if (CustomFields.Contains(field))
        {
            if (!customFieldDefinitionId.HasValue)
            {
                errors.Add($"{path}: CustomFieldDefinitionId is required for field '{field}'.");
                return;
            }

            if (customFieldTypes is null || !customFieldTypes.TryGetValue(customFieldDefinitionId.Value, out var dataType))
            {
                errors.Add($"{path}: custom field definition '{customFieldDefinitionId}' was not found or is not active.");
                return;
            }

            var allowedOps = ResolveCustomFieldOperators(dataType);
            if (!allowedOps.Contains(op))
            {
                errors.Add($"{path}: operator '{op}' is not allowed for custom field data type '{dataType}'.");
                return;
            }

            if (op == AgentLabelComparisonOperator.Regex)
            {
                if (value.Length > MaxRegexLength)
                    errors.Add($"{path}: regex pattern exceeds maximum length of {MaxRegexLength}.");
                else
                {
                    try { _ = new System.Text.RegularExpressions.Regex(value); }
                    catch (ArgumentException) { errors.Add($"{path}: invalid regex pattern."); }
                }
            }

            return;
        }

        errors.Add($"{path}: unsupported field '{field}'.");
    }

    private static HashSet<AgentLabelComparisonOperator> ResolveCustomFieldOperators(CustomFieldDataType dataType)
        => dataType switch
        {
            CustomFieldDataType.Integer
                or CustomFieldDataType.Decimal
                or CustomFieldDataType.Date
                or CustomFieldDataType.DateTime => NumericOperators,
            CustomFieldDataType.Boolean => StatusOperators,
            _ => TextOperators  // Text, Dropdown, ListBox
        };
}
