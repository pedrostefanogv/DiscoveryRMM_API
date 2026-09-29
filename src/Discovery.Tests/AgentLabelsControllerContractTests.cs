using Discovery.Api.Controllers;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Contrato do CONTROLLER de regras de label (o ponto exato da regressao B1).
///
/// A migracao para CQRS trocou o DTO de entrada por um command com
/// <c>ExpressionJson</c> (string) sem ajustar o front, que continuou enviando
/// <c>expression</c> (objeto): criar regra respondia 400. Estes testes exercitam a
/// action com o payload tipado do front e verificam a traducao para o command e a
/// resposta devolvida.
/// </summary>
public class AgentLabelsControllerContractTests
{
    private static readonly Guid RuleId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid UserId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Test]
    public async Task CreateRule_MapsExpressionObjectToCommand()
    {
        var mediator = new CapturingMediator();
        mediator.Responder = _ => Result<LabelRuleDto>.Success(new LabelRuleDto(
            RuleId, "r", "L", null, true, "ApplyAndRemove", "Exact", TextExpression(), null, DateTime.UtcNow, DateTime.UtcNow));
        var controller = BuildController(mediator);

        var result = await controller.CreateRule(new CreateAgentLabelRuleRequest
        {
            Name = "Windows PROD",
            Label = "PROD",
            ApplyMode = AgentLabelApplyMode.ApplyAndRemove,
            LabelMatch = AgentLabelLabelMatch.Prefix,
            Expression = TextExpression()
        });

        var command = mediator.LastRequest as CreateLabelRuleCommand;
        Assert.That(command, Is.Not.Null, "A action deve enviar CreateLabelRuleCommand ao mediator.");
        var serialized = Discovery.Core.Helpers.AgentLabelExpressionJson.DeserializeOrDefault(command!.ExpressionJson);
        Assert.That(serialized.Children, Has.Count.EqualTo(1),
            "A expressao (objeto) precisa ser serializada para o command.");
        Assert.That(serialized.Children[0].Field, Is.EqualTo(AgentLabelField.OperatingSystem));
        Assert.That(command.CreatedBy, Is.EqualTo("user:" + UserId), "O ator vem do usuario autenticado.");
        Assert.That(command.LabelMatch, Is.EqualTo("Prefix"), "O alvo do modo Remove precisa ser repassado.");

        Assert.That(result, Is.InstanceOf<CreatedAtActionResult>());
        var created = (LabelRuleDto)((CreatedAtActionResult)result).Value!;
        Assert.That(created.Expression.Children, Has.Count.EqualTo(1),
            "A resposta devolve a expressao como objeto, nao como expressionJson.");
    }

    [Test]
    public async Task UpdateRule_MapsExpressionObjectToCommand()
    {
        var mediator = new CapturingMediator();
        mediator.Responder = _ => Result<LabelRuleDto>.Success(new LabelRuleDto(
            RuleId, "r2", "L2", null, true, "ApplyOnly", "Exact", TextExpression(), null, DateTime.UtcNow, DateTime.UtcNow));
        var controller = BuildController(mediator);

        var result = await controller.UpdateRule(RuleId, new UpdateAgentLabelRuleRequest
        {
            Name = "r2",
            Label = "L2",
            IsEnabled = true,
            ApplyMode = AgentLabelApplyMode.ApplyOnly,
            Expression = TextExpression()
        });

        var command = mediator.LastRequest as UpdateLabelRuleCommand;
        Assert.That(command, Is.Not.Null);
        Assert.That(command!.Id, Is.EqualTo(RuleId));
        var serialized = Discovery.Core.Helpers.AgentLabelExpressionJson.DeserializeOrDefault(command.ExpressionJson);
        Assert.That(serialized.Children, Has.Count.EqualTo(1));
        Assert.That(serialized.Children[0].Field, Is.EqualTo(AgentLabelField.OperatingSystem));
        Assert.That(command.UpdatedBy, Is.EqualTo("user:" + UserId));
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetDistinct_PassesLimitToQuery()
    {
        var mediator = new CapturingMediator();
        mediator.Responder = _ => Result<IReadOnlyList<string>>.Success(["PROD"]);
        var controller = BuildController(mediator);

        await controller.GetDistinct(limit: 250);

        Assert.That(mediator.LastRequest, Is.InstanceOf<Discovery.Core.Cqrs.AgentLabels.Queries.GetDistinctLabelsQuery>());
        var query = (Discovery.Core.Cqrs.AgentLabels.Queries.GetDistinctLabelsQuery)mediator.LastRequest!;
        Assert.That(query.Limit, Is.EqualTo(250));
    }

    private static AgentLabelsController BuildController(IMediator mediator)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["UserId"] = UserId;

        return new AgentLabelsController(mediator, new NoopReprocessQueue())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };
    }

    private static AgentLabelRuleExpressionNodeDto TextExpression() => new()
    {
        NodeType = AgentLabelNodeType.Group,
        LogicalOperator = AgentLabelLogicalOperator.And,
        Children =
        [
            new AgentLabelRuleExpressionNodeDto
            {
                NodeType = AgentLabelNodeType.Condition,
                Field = AgentLabelField.OperatingSystem,
                Operator = AgentLabelComparisonOperator.Contains,
                Value = "Windows"
            }
        ]
    };

    private sealed class CapturingMediator : IMediator
    {
        public object? LastRequest { get; private set; }
        public Func<object, object>? Responder { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            var response = Responder?.Invoke(request!);
            return Task.FromResult((TResponse)(response ?? default(TResponse))!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            LastRequest = request;
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class NoopReprocessQueue : ILabelReprocessQueue
    {
        public ValueTask<string> EnqueueAsync(string? actor = null, CancellationToken cancellationToken = default, bool coalesce = true)
            => ValueTask.FromResult(Guid.NewGuid().ToString("N"));

        public Task<AgentLabelReprocessStatusResponse?> GetStatusAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.FromResult<AgentLabelReprocessStatusResponse?>(null);
    }
}
