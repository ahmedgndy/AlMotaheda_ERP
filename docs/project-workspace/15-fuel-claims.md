# Fuel claims: three camera photos and the total read on the phone

Module `AlMotaheda.Modules.Fleet` (schema `fleet`), Flutter `lib/features/fleet` and `lib/core/capture`.
Read `05-fleet.md`, `10-price-control.md` and `12-treasury-custody.md` first (vehicles, claims, custody, prices).
Other vehicle expenses (maintenance, tolls…) are unchanged: amount, odometer photo, receipt.

## 1. What the driver does

1. Opens **New fuel claim** on the vehicle's card. The vehicle and its fuel (the Price Control item) are shown,
   read-only. Nothing is chosen or typed.
2. Takes three photos with the camera (never the gallery), each after a short framing guide: the **receipt**, the
   **pump display close up**, the **vehicle beside the pump**. Each photo is checked on the phone, offline, and
   kept in the app's storage the moment it is accepted.
3. The phone reads the **total** from the pump display (and the litres and price per litre when the display shows
   them). Read reliably, it fills the total; nothing is typed.
4. Sends. Online it goes at once; offline it waits on the phone and is sent by itself when the server answers.

Removed from the fuel form: fuel selector, litres, price per litre, justification, station, receipt number, odometer
photo, date picker, notes. The columns stay (`VendorName`, `InvoiceNo`, `PriceJustification`, `OdometerKm`…) for the
claims made before and for the other expense types.

## 2. Text recognition (OCR)

- Engine: Google ML Kit text recognition, Latin script, **model bundled in the APK**
  (`com.google.mlkit:text-recognition:16.0.1` through `google_mlkit_text_recognition`). No download at the pump, no
  network, no key, no paid service. It reads digits and Latin letters; it does not read Arabic words.
- `PumpReader` (pure Dart, `lib/core/capture/pump_reader.dart`) picks the TOTAL, in order of trust:
  1. the display's own arithmetic: one amount equals litres × price per litre (0.5 % tolerance) → 0.95;
  2. a number labelled as the amount (TOTAL, AMOUNT, SALE, EGP, L.E, المبلغ…) when only one value is → 0.88; litres
     and price labelled beside it that don't give the total are dropped and the reading drops to 0.70;
  3. the clearly biggest digits (layout) → 0.72; the only number on the photo → 0.70.
  Litres, price-per-litre, counters (pump, nozzle, transaction), dates and times are never the total. Arabic-Indic
  digits and decimal marks are converted; O/I/S/B read inside numbers become 0/1/5/8. Amounts are exact decimals
  (`Fixed`), never doubles.
- From **0.80** the total fills itself (`source: Ocr`). Below, or with digits too small or cut off at the edge, the
  driver is asked to retake. After two unreliable photos (or on a phone without the engine) the driver may type the
  total: sent as `ManualCorrection` with the phone's reading kept beside it, and flagged for the reviewer.
- Unlabelled litres and price whose order can't be told apart (15.50 × 40.00) are not sent: the price check is then
  Incomplete rather than wrong.
- Measured on the emulator (API 37 image, airplane mode, `integration_test/fuel_ocr_test.dart`): plain-font
  display and "EGP 500.00" read as 500.00 (0.95 / 0.88); a seven-segment display read the total right but litres and
  price wrong (36.29, 19.90) → not trusted; blurred and no-amount photos refused. These are generated images: real
  pump displays (glare, angles, seven-segment fonts) still need a field check on drivers' phones.

## 3. Photo checks on the phone (no network)

`checkFuelPhoto` (`lib/features/fleet/presentation/fuel_capture.dart`): brightness and Laplacian focus measure
(`photo_quality.dart`) for every photo, severe thresholds dark < 35, bright > 248, blur < 18, small < 600 px;
a receipt with fewer than two readable text lines; a pump photo without a reliable total. A severe problem means
retake; after two tries the driver may keep the photo, and the problem goes to the reviewer (`reading.photoNotes`
→ flag `PhotoQuality`). The checks help the evidence be readable; they don't prove it is genuine.

## 4. API

`POST /api/vehicle-expenses` with `type: Fuel`:

```json
{ "vehicleId": 2, "type": "Fuel", "capturedAt": "2026-10-24T08:03:54Z", "amount": 500.00,
  "reading": { "source": "Ocr", "ocrAmount": 500.00, "ocrLiters": 36.23, "ocrUnitPrice": 13.80,
               "confidence": 0.95, "engine": "mlkit-latin-16.0.1", "text": "…", "photoNotes": null },
  "invoiceAttachmentId": 11, "pumpDisplayAttachmentId": 12, "vehicleAtPumpAttachmentId": 13,
  "idempotencyKey": "…" }
```

- `capturedAt` is required: when the photos were taken (UTC). The expense date is its company date; a typed
  `expenseDate` must agree. `SubmittedAt` is when the server received it.
- Typed `fuelQuantity`, `unitPrice` or `priceItemId` → 400. `source: Ocr` needs `ocrAmount == amount` and a
  confidence → else 400 (a corrected amount is `ManualCorrection`).
- The three photos (purposes `Receipt`/`Invoice`, `PumpDisplay`, `VehicleAtPump`), uploaded by the caller and bound
  to nothing else → else 400/422 `evidence_required`.
- `GET /api/vehicles/{id}/fuel-claim` (fuel View or Create; the vehicle's current driver or fleet View / fuel Approve
  / See money): the vehicle's fuel item, `canClaim`, the `outstanding` fuel claim (anyone's: number and status only)
  and the caller's `latest`.
- `POST /api/vehicle-expenses/{id}/resubmit` (returned for correction): the photos retaken (`invoiceAttachmentId`,
  `pumpDisplayAttachmentId`, `vehicleAtPumpAttachmentId`, earlier photos kept) and, with a new pump photo, a new
  `amount` + `reading`: the price check, the pending custody entry and any price case follow it.
- `PUT /api/vehicles/{id}`: optional `fuelPriceItemId` (an active litre item); left unchanged when not sent.

## 5. Price Control

- The vehicle's fuel is `Vehicle.FuelPriceItemId` when a fleet manager linked one; else the only active litre item
  of the Fuel category whose English name starts with the vehicle's fuel type (Diesel). Several petrol grades and no
  link → no item.
- The server computes `EffectiveUnitPrice = amount ÷ litres` (4 decimals) and runs the existing check on the expense
  date: Block policy → 409 `price_above_range`; ExecutiveApproval → a price case on a project, else the claim is
  Escalated. No justification is asked of the driver any more (the case carries a fixed reason).
- No litres, no fuel item, or a unit mismatch → `PriceCheck = Incomplete`, flag `PriceNotValidated`. Never a pass.
- The comparison is stored as made (`PriceCheck`, `ReferenceMaxPrice`, `EffectiveUnitPrice`); a later range doesn't
  rewrite it. Approval still waits for an open price case, as before.

## 6. One fuel claim per vehicle waiting

- A vehicle has at most one fuel claim in `Submitted`, `Escalated` or `EvidenceRequested` (returned for correction).
  Another is refused with **409 `fuel_claim_outstanding`** (`claimId`, `claimNumber`, `claimStatus` in the body):
  checked before anything else, again under the vehicle's application lock, and enforced by the filtered unique
  index `UX_VehicleExpense_OutstandingFuel` on `VehicleId` for camera claims (`CapturedAt IS NOT NULL`). Claims of the
  earlier form are outside the index so it builds over existing data; the locked check covers them.
- It is per vehicle: another driver, a fleet manager on the driver's behalf, another phone or a second tap are
  refused the same way. Approved or Rejected frees the vehicle. A returned claim is resubmitted, never replaced.
- The phone shows the same rule on the vehicle's card, with words and icons, not colour alone: Draft, Pending sync,
  Waiting for connection, Submitted (pending review), Returned for correction, Approved, Rejected, Conflict, Not
  accepted. A queued claim on the phone locks the vehicle there too. If the server already has a waiting claim when a
  queued one arrives, it becomes a **conflict**: kept on the phone with its photos until the driver sends it again
  (after the review) or discards it.

## 7. Offline and sync (Flutter)

- The draft is saved in the encrypted device database (`sync_meta`, key `fuel.draft.<vehicleId>`) after every photo:
  leaving the screen or a restart loses nothing. Its moment is the first photo's.
- Sending: online it is posted at once (a refusal shows on the form, the draft stays); offline, or when the server
  doesn't answer, it becomes one outbox operation (`fleet.claim.create`) that owns its three photo files. Each
  confirmed upload is remembered (`fuel.upload.<photoKey>`) so a retry sends only what the server lacks; the claim and
  every photo carry idempotency keys, so a resend makes no second claim, attachment or alert.

## 8. Migration `CameraFuelClaims`

Nullable columns only: `VehicleExpense.CapturedAt, AmountSource, OcrAmount, OcrLiters, OcrUnitPrice, OcrConfidence,
OcrEngine, OcrText, EffectiveUnitPrice`, `Vehicle.FuelPriceItemId`; attachment purposes `PumpDisplay`, `VehicleAtPump`;
flag kinds `PriceNotValidated, AmountManuallyEntered, PumpReadingInconsistent, LowReadingConfidence, PhotoQuality`;
`CK_VehicleExpense_Fuel` widened for camera claims (earlier claims satisfy its first branch unchanged); the filtered
unique index. Rehearsed as the idempotent script (twice) on a copy of the demo database that holds an earlier
outstanding fuel claim.

## 9. Release order

The API refuses fuel claims of the earlier form (typed litres and price, odometer photo). Release the backend
together with the app version that has this form; drivers on app 1.1.0 can still send other expenses, but their fuel
claims are refused with a validation error until they update.
