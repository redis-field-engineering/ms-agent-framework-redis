export const handler = async (event) => {
  const attributes = event.request.userAttributes ?? {};
  const tenantId = attributes["custom:tenant_id"];
  const travelerId = attributes["custom:traveler_id"];

  if (!tenantId || !travelerId) {
    throw new Error("Cognito user is missing custom:tenant_id or custom:traveler_id");
  }

  event.response = event.response ?? {};
  event.response.claimsAndScopeOverrideDetails = {
    ...(event.response.claimsAndScopeOverrideDetails ?? {}),
    accessTokenGeneration: {
      claimsToAddOrOverride: {
        tenant_id: tenantId,
        traveler_id: travelerId,
      },
    },
  };

  return event;
};
