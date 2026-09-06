# Continuous custom-curve evaluation

`BezierCurve.CaptureEvaluator()` returns one immutable `NormalizedEvaluator`.
Capture it once for a coupled stick operation and call its
`TryEvaluateNormalized(double input, out double output)` for both finite
normalized magnitudes. The mapper remains responsible for sign, directional
caps and the final target encoding. The public convenience method captures
the current evaluator for a single value.

Cold `InitBezierCurve` builds and publishes the evaluator after the original
byte lookup table is initialized. `AsString` and `CustomDefinition` alone do
not publish changes. A captured predecessor cannot observe new coefficients
or sample data halfway through an operation. Evaluation does not read mutable
LUT/compiler fields, allocate, lock, or perform I/O.

The implementation preserves the local source's GRE/Mika-N MIT attribution
and curve semantics: fixed endpoints `(0,0)`/`(1,1)`, custom X controls in
`[0,1]`, output clamping for Y overshoot, linear definitions, and special
`99,91..95` enhanced-precision/quadratic/cubic/ease-out formulas. The continuous
inverse uses eleven cold samples, at most eight safeguarded Newton steps and
thirty-two bisection steps. This is not interpolation of a byte table.
Residuals are evaluated around the midpoint or reversed endpoint to avoid
rounding a nearly flat X value to the requested input before subtracting.
The exact degenerate cubic X forms `(0,0)`, `(1,1)` and `(1,0)` use their
closed-form cube-root inverses. Y near the midpoint is also centered, preserving
opposing large-control behavior before the existing output clamp.

Nonfinite or out-of-range normalized input returns `false` and output zero.
Invalid/nonfinite numeric controls or overflowing coefficients return `false`
while preserving a valid input as linear fallback. The caller must not turn a
valid stick magnitude into center merely because its custom curve is invalid.
Before initialization, the evaluator is linear.
The existing profile-string parser is unchanged; this API does not introduce
a different interpretation of strings accepted by legacy initialization.

The legacy byte LUT algorithm, its original inverse iterations, rounding,
truncation and asymmetric stick mirroring are unchanged. Continuous values
are not promised to equal a rounded byte at every location; retaining the
original sub-byte information is the purpose of the new API.

`BezierCurveNormalizedTests` checks independent inverse results across the
complete twelve-bit magnitude grid, distinct sixteen-bit magnitudes, flat
slopes/endpoints, special modes, overshoot, rejection/fallback behavior,
immutable paired capture, zero managed hot-path allocations and every legacy
byte against the frozen original LUT solver. These are source/numeric tests,
not end-to-end input-latency or hardware validation.

The mapped Switch 2 scroll-tap, direction-tap and stick-assist lanes likewise
accept finite `double` profile coordinates in `[0,255]`. Byte callers convert
exactly. Geometry, scroll steps and assist velocity do not first round to a
byte. Existing center128, scroll3% radial deadzone, assist inclusive127..129
neutral band, timing and source/profile lifetime rules are unchanged. Invalid
coordinates clear the lane baseline and require a fresh baseline before
emission. `Switch2MappedStickLanePrecisionTests` covers fractional boundaries,
all legacy byte-sector pairs and byte assist magnitudes, pulse expiry,
standalone selection, rebaselining and allocation-free execution.
