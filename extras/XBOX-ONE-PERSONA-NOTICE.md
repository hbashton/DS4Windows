# Experimental Windows Xbox controller identity

`xbox-one-authorized-persona.json` is the maintainer-selected deployment
configuration for VIIPER's Xbox One/Series virtual output on Windows. RC4.5.1
includes the exact configuration used by the existing portable controller lab;
no identity fields, descriptor timing or manufacturer strings were changed.

SHA-256: `2A85D3395529C7305F55338E4965A7FFD4E269DDE535FA41BD98BC54D67111C4`

The synthetic USB identity is F00D:BEED, with the existing strings
`VIIPER Portable Lab` and `VIIPER GIP Test Controller`. The JSON contains USB/GIP
descriptors and an explicit deployment authorization decision, not a password,
cryptographic key or real Xbox console-authentication credential. Its inclusion
does not establish USB VID/PID allocation rights or Microsoft certification.

Keep the JSON beside DS4Windows: selecting Xbox One/Series output needs it.
`derivePerRegistrationIdentity` remains enabled to distinguish simultaneous and
recreated virtual devices. This creates new Windows per-device associations
when a virtual device is recreated rather than promising a persistent identity.

Windows XInput/WGI input and feedback were exercised using this configuration
on the development machine. Those observations are not a complete hardware,
Windows-version or game compatibility guarantee. Real Xbox console
authentication is not supported. See `docs/protocols/xbox-one-semantic-egress-v1.md`
and the dated controller-platform validation ledgers for the contract and evidence.
