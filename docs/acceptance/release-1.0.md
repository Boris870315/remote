# 1.0 release acceptance

Status: **not released**.

This checklist tracks publication readiness separately from 1.0 feature progress. Every item remains a release gate, but none contributes to the feature-progress percentage.

## Windows publication

- [ ] Produce a Windows installer from the accepted release commit.
- [ ] Sign the application and installer with the Windows code-signing identity.
- [ ] Install and launch successfully on a clean supported Windows computer.

## macOS publication

- [ ] Produce the macOS `.app` and DMG from the accepted release commit.
- [ ] Sign with Apple Developer ID and complete notarization.
- [ ] Bundle the required FreeRDP native runtime correctly.
- [ ] Install and launch successfully on a clean supported Mac.

## Legal and reproducibility

- [ ] Include third-party license notices, including the FreeRDP Apache-2.0 notice.
- [ ] Merge the accepted commit to the release branch and rebuild every final artifact from that exact commit.
- [ ] Record release artifact hashes and retain the matching CI/build result.
