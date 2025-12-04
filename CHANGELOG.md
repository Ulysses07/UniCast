# Changelog

All notable changes to UniCast will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Comprehensive unit test suite (171 tests)
- README.md documentation
- CHANGELOG.md tracking

### Changed
- Nullable reference types enabled across all projects

## [1.0.0-beta.21] - 2024-12-04

### Added
- Hardware encoder auto-detection (NVENC, AMF, QSV)
- FrameBufferPool for memory-efficient frame handling
- Integration tests for critical paths
- Interface abstractions for better testability

### Changed
- Migrated to Microsoft.Extensions.DependencyInjection
- HttpClient refactored to use IHttpClientFactory pattern
- Improved rate limiting in ChatBus (100ms per platform)

### Fixed
- Socket exhaustion from HttpClient instances
- Memory leaks in OpenCV Mat handling
- Thread safety issues in StreamController
- FFmpeg process cleanup on application exit

## [1.0.0-beta.20] - 2024-12-03

### Added
- License server deployment on Hostinger VPS
- Docker containerization for license server
- HTTPS/SSL configuration with Let's Encrypt
- Health check endpoints

### Changed
- Simplified licensing model (Trial + Lifetime only)
- License validation with hardware fingerprint

### Fixed
- Application startup crashes
- License validation timeout issues

## [1.0.0-beta.19] - 2024-12-02

### Added
- Stream key masking in logs (security)
- Dynamic log level switching
- Crash reporter with local storage
- Configuration validation on startup

### Changed
- Improved error handling in all services
- Better async/await patterns with ConfigureAwait

### Fixed
- UI thread blocking during stream start
- Race conditions in chat message processing

## [1.0.0-beta.18] - 2024-12-01

### Added
- Rate limiting per HardwareId in license server
- Suspicious activity detection
- Atomic file operations with retry logic

### Security
- Environment variables for all secrets
- No hardcoded API keys or passwords
- Secure hardware fingerprint generation

## [1.0.0-beta.17] - 2024-11-30

### Added
- Orphan FFmpeg process cleanup on startup
- Graceful shutdown with resource cleanup
- Event handler cleanup to prevent memory leaks

### Changed
- Improved FFmpeg Named Pipe communication
- Better overlay system integration

### Fixed
- FFmpeg processes not terminating on app close
- Memory leaks from event subscriptions

## [1.0.0-beta.16] - 2024-11-29

### Added
- Multi-platform chat aggregation
- YouTube chat ingestor
- TikTok chat ingestor
- Instagram hybrid chat ingestor
- Facebook chat ingestor

### Changed
- ChatBus singleton pattern with thread safety
- Platform-specific rate limiting

## [1.0.0-beta.15] - 2024-11-28

### Added
- Hardware encoder service
- NVIDIA NVENC support
- AMD AMF support
- Intel Quick Sync Video support

### Changed
- Encoder selection based on availability
- Fallback to software encoding

## [1.0.0-beta.14] - 2024-11-27

### Added
- Preview service for camera display
- Audio level monitoring
- Device enumeration service

### Fixed
- Camera preview freezing
- Audio device switching issues

## [1.0.0-beta.13] - 2024-11-26

### Added
- MVVM architecture implementation
- ViewModels for all major views
- Dependency injection setup

### Changed
- Separated UI from business logic
- Improved testability

## [1.0.0-beta.12] - 2024-11-25

### Added
- Settings persistence (JSON)
- Platform target configuration
- Stream quality presets

### Changed
- Centralized configuration in AppConstants

## [1.0.0-beta.11] - 2024-11-24

### Added
- Serilog structured logging
- Rolling file logs
- Debug output in development

### Changed
- Consistent log message format
- Log file size limits

## [1.0.0-beta.10] - 2024-11-23

### Added
- Basic streaming functionality
- YouTube RTMP support
- Twitch RTMP support

### Known Issues
- TikTok streaming requires manual RTMP URL
- Instagram requires third-party tools

---

## Version History Summary

| Version | Date | Highlights |
|---------|------|------------|
| beta.21 | 2024-12-04 | Test suite, documentation |
| beta.20 | 2024-12-03 | License server deployment |
| beta.19 | 2024-12-02 | Security improvements |
| beta.18 | 2024-12-01 | Rate limiting |
| beta.17 | 2024-11-30 | Resource cleanup |
| beta.16 | 2024-11-29 | Chat aggregation |
| beta.15 | 2024-11-28 | Hardware encoding |
| beta.14 | 2024-11-27 | Preview & audio |
| beta.13 | 2024-11-26 | MVVM architecture |
| beta.12 | 2024-11-25 | Settings system |
| beta.11 | 2024-11-24 | Logging |
| beta.10 | 2024-11-23 | Initial streaming |

[Unreleased]: https://github.com/your-org/unicast/compare/v1.0.0-beta.21...HEAD
[1.0.0-beta.21]: https://github.com/your-org/unicast/compare/v1.0.0-beta.20...v1.0.0-beta.21
[1.0.0-beta.20]: https://github.com/your-org/unicast/compare/v1.0.0-beta.19...v1.0.0-beta.20
[1.0.0-beta.19]: https://github.com/your-org/unicast/compare/v1.0.0-beta.18...v1.0.0-beta.19
[1.0.0-beta.18]: https://github.com/your-org/unicast/compare/v1.0.0-beta.17...v1.0.0-beta.18
[1.0.0-beta.17]: https://github.com/your-org/unicast/releases/tag/v1.0.0-beta.17
