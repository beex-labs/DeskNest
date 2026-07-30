# Disclaimer

BeeX DeskNest is provided "as is", without warranty of any kind, under the terms
of the Apache License 2.0 (see [LICENSE](LICENSE)). This document clarifies how
the application interacts with third-party content and with your system.

## Third-party content and services

Some optional features retrieve data from third-party or public endpoints:

- **Lyrics** (Kugou, NetEase, QQ Music, LrcLib, and local player caches)
- **Album artwork**
- **Weather**
- **Text translation** (for captured / OCR'd text)
- **On-demand components** (FFmpeg and the OCR engine, downloaded at first use)

All such content — including lyrics, artwork, and translations — remains the
property of its respective owners, authors, and platforms. BeeX DeskNest only
retrieves and displays it for the user's personal convenience and caches it
locally; it claims no rights over this content and does not redistribute it.

You are solely responsible for ensuring that your use of these services complies
with each provider's Terms of Service and with the copyright laws applicable in
your jurisdiction.

## Lyrics copyright

Song lyrics are the intellectual property of their original authors, publishers,
and/or the platforms that host them. BeeX DeskNest displays lyrics for personal,
non-commercial listening convenience only, and does not host or redistribute any
lyric database.

## Screen capture, recording and OCR — local processing

Screenshots, screen recordings, scrolling captures and OCR text recognition are
processed **locally on your machine**. Captured content is saved to your local
BeeX data folder and is **not uploaded by BeeX DeskNest**.

Exception: if you explicitly use the *translation* feature on captured or
recognized text, that text (not the image) is sent to a third-party translation
service in order to return the translation.

## System cleaner (BeeXCleaner)

The system cleaner performs privileged operations — uninstalling programs,
deleting residual files, backing up / restoring registry keys, and wiping free
disk space — only when you explicitly trigger them and grant administrator
elevation. These actions modify your system and may be irreversible. Use them at
your own risk and keep backups of important data.

## Registry and startup

BeeX DeskNest reads a machine identifier, may register itself to start with
Windows (only at your request), and sets its AppUserModelID. It does not
otherwise modify system-critical registry areas.

## No warranty

To the maximum extent permitted by applicable law, the authors and contributors
shall not be liable for any damages arising from the use of this software. See
the [LICENSE](LICENSE) for the full warranty disclaimer and limitation of
liability.
