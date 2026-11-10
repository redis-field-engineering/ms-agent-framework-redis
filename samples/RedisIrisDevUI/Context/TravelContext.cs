// Copyright (c) Redis, Inc.

using System.Text.Json.Serialization;
using Redis.Iris.ContextRetriever.Modeling;

namespace RedisIrisDevUI;

internal static class TravelContext
{
    internal const string DemoTenantId = "redis-travel-demo";

    internal static ContextDataModel Model { get; } = ContextModelCompiler.Compile(
        "Redis Iris travel agency",
        "Structured traveler, flight, hotel, and receipt context for the DevUI sample.",
        typeof(Traveler),
        typeof(FlightReservation),
        typeof(HotelReservation),
        typeof(TravelReceipt));

    internal static TravelContextSeed Examples { get; } = new(
        Travelers:
        [
            new Traveler
            {
                TenantId = DemoTenantId,
                Id = "traveler-001",
                TravelerId = "traveler-001",
                FullName = "Avery Morgan",
                LoyaltyTier = "gold",
                HomeAirport = "BOS",
            },
            new Traveler
            {
                TenantId = DemoTenantId,
                Id = "traveler-002",
                TravelerId = "traveler-002",
                FullName = "Riley Chen",
                LoyaltyTier = "silver",
                HomeAirport = "SEA",
            },
        ],
        Flights:
        [
            new FlightReservation
            {
                TenantId = DemoTenantId,
                Id = "flight-001",
                TravelerId = "traveler-001",
                ConfirmationCode = "IRIS42",
                Status = "confirmed",
                Carrier = "Redis Air",
                FlightNumber = "RA204",
                OriginAirport = "BOS",
                DestinationAirport = "SFO",
                DepartureTime = new DateTimeOffset(2027, 2, 8, 13, 30, 0, TimeSpan.Zero),
                ArrivalTime = new DateTimeOffset(2027, 2, 8, 20, 5, 0, TimeSpan.Zero),
                CabinClass = "business",
            },
            new FlightReservation
            {
                TenantId = DemoTenantId,
                Id = "flight-002",
                TravelerId = "traveler-002",
                ConfirmationCode = "IRIS84",
                Status = "confirmed",
                Carrier = "Redis Air",
                FlightNumber = "RA108",
                OriginAirport = "SEA",
                DestinationAirport = "JFK",
                DepartureTime = new DateTimeOffset(2027, 3, 14, 15, 10, 0, TimeSpan.Zero),
                ArrivalTime = new DateTimeOffset(2027, 3, 14, 20, 35, 0, TimeSpan.Zero),
                CabinClass = "economy",
            },
        ],
        Hotels:
        [
            new HotelReservation
            {
                TenantId = DemoTenantId,
                Id = "hotel-001",
                TravelerId = "traveler-001",
                ConfirmationCode = "HTL-6379",
                Status = "confirmed",
                PropertyName = "The Golden Gate Hotel",
                City = "San Francisco",
                CheckIn = new DateTimeOffset(2027, 2, 8, 15, 0, 0, TimeSpan.FromHours(-8)),
                CheckOut = new DateTimeOffset(2027, 2, 12, 11, 0, 0, TimeSpan.FromHours(-8)),
                RoomType = "king",
            },
            new HotelReservation
            {
                TenantId = DemoTenantId,
                Id = "hotel-002",
                TravelerId = "traveler-002",
                ConfirmationCode = "HTL-6380",
                Status = "confirmed",
                PropertyName = "The Manhattan Redis",
                City = "New York",
                CheckIn = new DateTimeOffset(2027, 3, 14, 16, 0, 0, TimeSpan.FromHours(-4)),
                CheckOut = new DateTimeOffset(2027, 3, 17, 11, 0, 0, TimeSpan.FromHours(-4)),
                RoomType = "queen",
            },
        ],
        Receipts:
        [
            new TravelReceipt
            {
                TenantId = DemoTenantId,
                Id = "receipt-001",
                TravelerId = "traveler-001",
                FlightReservationId = "flight-001",
                Category = "flight",
                Merchant = "Redis Air",
                Description = "Round-trip airfare from Boston to San Francisco",
                Amount = 1289.40m,
                Currency = "USD",
                PurchasedAt = new DateTimeOffset(2026, 10, 2, 14, 20, 0, TimeSpan.Zero),
            },
            new TravelReceipt
            {
                TenantId = DemoTenantId,
                Id = "receipt-002",
                TravelerId = "traveler-001",
                HotelReservationId = "hotel-001",
                Category = "hotel",
                Merchant = "The Golden Gate Hotel",
                Description = "Four-night hotel reservation",
                Amount = 1840.00m,
                Currency = "USD",
                PurchasedAt = new DateTimeOffset(2026, 10, 2, 14, 24, 0, TimeSpan.Zero),
            },
            new TravelReceipt
            {
                TenantId = DemoTenantId,
                Id = "receipt-003",
                TravelerId = "traveler-002",
                FlightReservationId = "flight-002",
                Category = "flight",
                Merchant = "Redis Air",
                Description = "Round-trip airfare from Seattle to New York",
                Amount = 642.15m,
                Currency = "USD",
                PurchasedAt = new DateTimeOffset(2026, 10, 4, 17, 5, 0, TimeSpan.Zero),
            },
        ]);

    internal static IReadOnlyList<TravelUserProvisioningProfile> Users { get; } =
    [
        new("avery-demo", "Avery Morgan", DemoTenantId, "traveler-001"),
        new("riley-demo", "Riley Chen", DemoTenantId, "traveler-002"),
    ];

    internal static TravelContextSeed ExamplesFor(TravelUser user) => new(
        Examples.Travelers.Where(item => item.TenantId == user.TenantId && item.TravelerId == user.TravelerId).ToArray(),
        Examples.Flights.Where(item => item.TenantId == user.TenantId && item.TravelerId == user.TravelerId).ToArray(),
        Examples.Hotels.Where(item => item.TenantId == user.TenantId && item.TravelerId == user.TravelerId).ToArray(),
        Examples.Receipts.Where(item => item.TenantId == user.TenantId && item.TravelerId == user.TravelerId).ToArray());
}

internal sealed record TravelContextSeed(
    IReadOnlyList<Traveler> Travelers,
    IReadOnlyList<FlightReservation> Flights,
    IReadOnlyList<HotelReservation> Hotels,
    IReadOnlyList<TravelReceipt> Receipts);

[ContextEntity(KeyTemplate = "traveler:{id}", Description = "A travel agency customer")]
internal sealed class Traveler
{
    [ContextField(Name = "tenant_id", Index = ContextIndex.Tag, AccessTag = "tenant_id", Description = "Tenant authorization boundary")]
    public required string TenantId { get; init; }

    [ContextField(Name = "id", IsKeyComponent = true, Description = "Stable traveler ID")]
    public required string Id { get; init; }

    [ContextField(Name = "traveler_id", Index = ContextIndex.Tag, AccessTag = "traveler_id", Description = "Traveler authorization boundary")]
    public required string TravelerId { get; init; }

    [ContextField(Name = "full_name", Index = ContextIndex.Text, Description = "Traveler's full name")]
    public required string FullName { get; init; }

    [ContextField(Name = "loyalty_tier", Index = ContextIndex.Tag, AllowedValues = ["standard", "silver", "gold", "platinum"], Description = "Travel loyalty tier")]
    public required string LoyaltyTier { get; init; }

    [ContextField(Name = "home_airport", Index = ContextIndex.Tag, Description = "Preferred origin airport IATA code")]
    public required string HomeAirport { get; init; }
}

[ContextEntity(KeyTemplate = "flight-reservation:{id}", Description = "A booked airline itinerary")]
internal sealed class FlightReservation
{
    [ContextField(Name = "tenant_id", Index = ContextIndex.Tag, AccessTag = "tenant_id", Description = "Tenant authorization boundary")]
    public required string TenantId { get; init; }

    [ContextField(Name = "id", IsKeyComponent = true, Description = "Stable flight reservation ID")]
    public required string Id { get; init; }

    [ContextField(Name = "traveler_id", Index = ContextIndex.Tag, AccessTag = "traveler_id", Description = "Traveler who owns the reservation")]
    public required string TravelerId { get; init; }

    [ContextField(Name = "confirmation_code", Index = ContextIndex.Tag, Description = "Airline confirmation code")]
    public required string ConfirmationCode { get; init; }

    [ContextField(Index = ContextIndex.Tag, AllowedValues = ["held", "confirmed", "cancelled", "completed"], Description = "Reservation status")]
    public required string Status { get; init; }

    [ContextField(Index = ContextIndex.Text, Description = "Operating airline")]
    public required string Carrier { get; init; }

    [ContextField(Name = "flight_number", Index = ContextIndex.Tag, Description = "Carrier flight number")]
    public required string FlightNumber { get; init; }

    [ContextField(Name = "origin_airport", Index = ContextIndex.Tag, Description = "Origin IATA airport code")]
    public required string OriginAirport { get; init; }

    [ContextField(Name = "destination_airport", Index = ContextIndex.Tag, Description = "Destination IATA airport code")]
    public required string DestinationAirport { get; init; }

    [ContextField(Name = "departure_time", Description = "Scheduled departure time")]
    public DateTimeOffset DepartureTime { get; init; }

    [ContextField(Name = "arrival_time", Description = "Scheduled arrival time")]
    public DateTimeOffset ArrivalTime { get; init; }

    [ContextField(Name = "cabin_class", Index = ContextIndex.Tag, AllowedValues = ["economy", "premium_economy", "business", "first"], Description = "Booked cabin")]
    public required string CabinClass { get; init; }

    [JsonIgnore]
    [ContextRelationship(typeof(Traveler), Name = "traveler", SourceField = "traveler_id", Description = "Traveler who owns the flight")]
    public Traveler? Traveler { get; init; }
}

[ContextEntity(KeyTemplate = "hotel-reservation:{id}", Description = "A booked hotel stay")]
internal sealed class HotelReservation
{
    [ContextField(Name = "tenant_id", Index = ContextIndex.Tag, AccessTag = "tenant_id", Description = "Tenant authorization boundary")]
    public required string TenantId { get; init; }

    [ContextField(Name = "id", IsKeyComponent = true, Description = "Stable hotel reservation ID")]
    public required string Id { get; init; }

    [ContextField(Name = "traveler_id", Index = ContextIndex.Tag, AccessTag = "traveler_id", Description = "Traveler who owns the reservation")]
    public required string TravelerId { get; init; }

    [ContextField(Name = "confirmation_code", Index = ContextIndex.Tag, Description = "Hotel confirmation code")]
    public required string ConfirmationCode { get; init; }

    [ContextField(Index = ContextIndex.Tag, AllowedValues = ["held", "confirmed", "cancelled", "completed"], Description = "Reservation status")]
    public required string Status { get; init; }

    [ContextField(Name = "property_name", Index = ContextIndex.Text, Description = "Hotel or property name")]
    public required string PropertyName { get; init; }

    [ContextField(Index = ContextIndex.Text, Description = "Destination city")]
    public required string City { get; init; }

    [ContextField(Name = "check_in", Description = "Check-in date and time")]
    public DateTimeOffset CheckIn { get; init; }

    [ContextField(Name = "check_out", Description = "Check-out date and time")]
    public DateTimeOffset CheckOut { get; init; }

    [ContextField(Name = "room_type", Index = ContextIndex.Tag, Description = "Booked room type")]
    public required string RoomType { get; init; }

    [JsonIgnore]
    [ContextRelationship(typeof(Traveler), Name = "traveler", SourceField = "traveler_id", Description = "Traveler who owns the hotel stay")]
    public Traveler? Traveler { get; init; }
}

[ContextEntity(KeyTemplate = "travel-receipt:{id}", Description = "A receipt for a travel purchase")]
internal sealed class TravelReceipt
{
    [ContextField(Name = "tenant_id", Index = ContextIndex.Tag, AccessTag = "tenant_id", Description = "Tenant authorization boundary")]
    public required string TenantId { get; init; }

    [ContextField(Name = "id", IsKeyComponent = true, Description = "Stable receipt ID")]
    public required string Id { get; init; }

    [ContextField(Name = "traveler_id", Index = ContextIndex.Tag, AccessTag = "traveler_id", Description = "Traveler associated with the purchase")]
    public required string TravelerId { get; init; }

    [ContextField(Name = "flight_reservation_id", Index = ContextIndex.Tag, Description = "Related flight reservation, when applicable")]
    public string? FlightReservationId { get; init; }

    [ContextField(Name = "hotel_reservation_id", Index = ContextIndex.Tag, Description = "Related hotel reservation, when applicable")]
    public string? HotelReservationId { get; init; }

    [ContextField(Index = ContextIndex.Tag, AllowedValues = ["flight", "hotel", "rail", "car", "fee", "other"], Description = "Expense category")]
    public required string Category { get; init; }

    [ContextField(Index = ContextIndex.Text, Description = "Merchant name")]
    public required string Merchant { get; init; }

    [ContextField(Index = ContextIndex.Text, Description = "Human-readable purchase description")]
    public required string Description { get; init; }

    [ContextField(Index = ContextIndex.Numeric, Sortable = true, Description = "Total charged amount")]
    public decimal Amount { get; init; }

    [ContextField(Index = ContextIndex.Tag, Description = "ISO 4217 currency code")]
    public required string Currency { get; init; }

    [ContextField(Name = "purchased_at", Description = "Purchase timestamp")]
    public DateTimeOffset PurchasedAt { get; init; }

    [JsonIgnore]
    [ContextRelationship(typeof(Traveler), Name = "traveler", SourceField = "traveler_id", Description = "Traveler associated with the receipt")]
    public Traveler? Traveler { get; init; }

    [JsonIgnore]
    [ContextRelationship(typeof(FlightReservation), Name = "flight_reservation", SourceField = "flight_reservation_id", Description = "Flight purchased by this receipt")]
    public FlightReservation? FlightReservation { get; init; }

    [JsonIgnore]
    [ContextRelationship(typeof(HotelReservation), Name = "hotel_reservation", SourceField = "hotel_reservation_id", Description = "Hotel stay purchased by this receipt")]
    public HotelReservation? HotelReservation { get; init; }
}
