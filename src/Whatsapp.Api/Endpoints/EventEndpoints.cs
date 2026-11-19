using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Whatsapp.Abstractions;
using Whatsapp.Abstractions.Bookings;
using Whatsapp.Api.Contracts;
using Whatsapp.Api.Infrastructure;
using Whatsapp.Api.Security;
using Whatsapp.Events;

namespace Whatsapp.Api.Endpoints;

internal static class EventEndpoints
{
    public static void MapEventEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/events").WithTags("Events");

        group.MapPost("/booking-created", BookingCreatedAsync)
            .RequireAuthorization(ApiPolicies.TenantKey)
            .WithName("PublishBookingCreated")
            .WithSummary("Publishes a booking-created event. The notification is queued, never sent inline.")
            .WithDescription("Re-posting the same event is safe: the notification is queued once per booking.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/booking-status-changed", BookingStatusChangedAsync)
            .RequireAuthorization(ApiPolicies.TenantKey)
            .WithName("PublishBookingStatusChanged")
            .WithSummary("Publishes a booking-status-changed event. Ignored when the customer did not opt in.")
            .WithDescription("Each (booking, status) pair is notified once.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<Results<Accepted<EventAcceptedResponse>, ValidationProblem>> BookingCreatedAsync(
        BookingSnapshotRequest request,
        ClaimsPrincipal user,
        IPhoneNumberNormalizer phones,
        IPublisher publisher,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request, phones);
        if (errors.Any)
        {
            return TypedResults.ValidationProblem(errors.ToDictionary());
        }

        var snapshot = ToSnapshot(request, user.GetTenantId()!);
        await publisher.Publish(new BookingCreatedNotification(snapshot), cancellationToken);

        return TypedResults.Accepted(
            (string?)null,
            new EventAcceptedResponse(EventKeys.BookingCreated, EventKeys.ReferenceForCreated(snapshot)));
    }

    private static async Task<Results<Accepted<EventAcceptedResponse>, ValidationProblem>> BookingStatusChangedAsync(
        BookingSnapshotRequest request,
        ClaimsPrincipal user,
        IPhoneNumberNormalizer phones,
        IPublisher publisher,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request, phones);
        if (errors.Any)
        {
            return TypedResults.ValidationProblem(errors.ToDictionary());
        }

        var snapshot = ToSnapshot(request, user.GetTenantId()!);
        await publisher.Publish(new BookingStatusChangedNotification(snapshot), cancellationToken);

        return TypedResults.Accepted(
            (string?)null,
            new EventAcceptedResponse(EventKeys.BookingStatusChanged, EventKeys.ReferenceForStatusChanged(snapshot)));
    }

    private static ValidationErrors Validate(BookingSnapshotRequest request, IPhoneNumberNormalizer phones)
    {
        var errors = new ValidationErrors();

        Require(errors, "bookingId", request.BookingId);
        Require(errors, "reference", request.Reference);
        Require(errors, "customerFirstName", request.CustomerFirstName);
        Require(errors, "companyName", request.CompanyName);

        if (!Enum.IsDefined(request.BookingType))
        {
            errors.Add("bookingType", "The booking type is not known.");
        }

        if (!Enum.IsDefined(request.Status))
        {
            errors.Add("status", "The booking status is not known.");
        }

        var phone = phones.Normalize(request.PhoneCountryCallingCode, request.PhoneNationalNumber);
        if (!phone.IsValid)
        {
            errors.Add("phoneNationalNumber", phone.FailureReason!);
        }

        return errors;
    }

    private static void Require(ValidationErrors errors, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(field, "The value is required.");
        }
    }

    private static BookingSnapshot ToSnapshot(BookingSnapshotRequest request, string tenantId) => new()
    {
        TenantId = tenantId,
        BookingId = request.BookingId,
        Reference = request.Reference,
        CustomerFirstName = request.CustomerFirstName,
        PhoneCountryCallingCode = request.PhoneCountryCallingCode,
        PhoneNationalNumber = request.PhoneNationalNumber,
        BookingType = request.BookingType,
        Status = request.Status,
        Details = request.Details,
        CompanyName = request.CompanyName,
        CreatedAt = request.CreatedAt,
        PreferredLanguage = request.PreferredLanguage,
        WhatsappUpdatesEnabled = request.WhatsappUpdatesEnabled,
    };
}
