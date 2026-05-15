using Contracts.Grpc;
using Grpc.Core;

internal sealed class InventoryGrpcService(InventoryStore store) : InventoryGrpc.InventoryGrpcBase
{
    public override Task<InventoryOperationReply> Check(InventoryOperationRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ProductId, out var productId))
        {
            return Task.FromResult(new InventoryOperationReply { Success = false, StatusCode = 400, Error = "Invalid product id." });
        }

        var (success, error) = InventoryOperations.TryCheck(store, productId, request.Quantity);
        return Task.FromResult(ToReply(success, error, "Insufficient stock."));
    }

    public override Task<InventoryOperationReply> Reserve(InventoryOperationRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ProductId, out var productId))
        {
            return Task.FromResult(new InventoryOperationReply { Success = false, StatusCode = 400, Error = "Invalid product id." });
        }

        var (success, error) = InventoryOperations.TryReserve(store, productId, request.Quantity);
        return Task.FromResult(ToReply(success, error, "Insufficient stock."));
    }

    public override Task<InventoryOperationReply> Release(InventoryOperationRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ProductId, out var productId))
        {
            return Task.FromResult(new InventoryOperationReply { Success = false, StatusCode = 400, Error = "Invalid product id." });
        }

        var (success, error) = InventoryOperations.TryRelease(store, productId, request.Quantity);
        return Task.FromResult(ToReply(success, error, "Cannot release more than reserved quantity."));
    }

    public override Task<InventoryOperationReply> Commit(InventoryOperationRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ProductId, out var productId))
        {
            return Task.FromResult(new InventoryOperationReply { Success = false, StatusCode = 400, Error = "Invalid product id." });
        }

        var (success, error) = InventoryOperations.TryCommit(store, productId, request.Quantity);
        return Task.FromResult(ToReply(success, error, "Cannot commit more than reserved quantity."));
    }

    private static InventoryOperationReply ToReply(bool success, string? errorCode, string validationError) =>
        success
            ? new InventoryOperationReply { Success = true, StatusCode = 200 }
            : errorCode switch
            {
                "not_found" => new InventoryOperationReply { Success = false, StatusCode = 404, Error = "Product not found." },
                _ => new InventoryOperationReply { Success = false, StatusCode = 400, Error = validationError }
            };
}
