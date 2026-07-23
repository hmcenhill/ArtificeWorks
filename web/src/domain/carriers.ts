// The carriers the visitor can pick when booking a shipment (11.3). Mirrored by hand from
// ShippingConfiguration.DefaultCarriers on the server — there is no carriers endpoint, and these
// are the world's shape rather than a demo dial, so they change about never. If a factory ever
// configures its own carrier list (Shipping:Carriers), this list would be stale; the API stays the
// authority, so a name that isn't recognised comes back as `unknown_carrier` and is shown as such.
export const KNOWN_CARRIERS: readonly string[] = [
  "Ravenscroft Haulage",
  "Meridian Aether Post",
  "Kettleby & Sons Carriage",
];
