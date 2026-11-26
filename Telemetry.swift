import Darwin
import Foundation

struct QuotaSample: Codable, Equatable, Sendable {
    let observedAt: Date
    let resetsAt: Date
    let windowMinutes: Int
    let usedPercent: Double
}

struct ModelOption: Sendable {
    let id: String
    let name: String
    let efforts: [String]
    let defaultEffort: String?
    let isDefault: Bool
    var supportsFastMode: Bool? = nil

    static func fastModeSupport(serviceTierIDs: [String]?, additionalSpeedTiers: [String]?) -> Bool? {
        guard serviceTierIDs != nil || additionalSpeedTiers != nil else { return nil }
        return serviceTierIDs?.contains { ["priority", "fast"].contains($0.lowercased()) } == true
            || additionalSpeedTiers?.contains { $0.lowercased() == "fast" } == true
    }

    static func enrichFastSupport(_ models: [Self], from catalog: [Self]) -> [Self] {
        models.map { model in
            var result = model
            result.supportsFastMode = model.supportsFastMode ?? catalog.first { $0.id == model.id }?.supportsFastMode
            return result
        }
    }
}

struct Activity: Sendable {
    let observedAt: Date
    let model: String?
    let effort: String?
    let turns: Int
    let tokens: Int
}

struct Telemetry: Sendable {
    let quota: QuotaSample?
    let shortQuota: QuotaSample?
    let history: [QuotaSample]
    let activity: Activity?
    let models: [ModelOption]
    let source: String
    let error: String?
}

func readTelemetry(now: Date, force: Bool = false) -> Telemetry {
    TelemetryReader.shared.read(now: now, force: force)
}

private let weeklyMinutes = 7 * 24 * 60

private struct RolloutRecord: Decodable {
    let timestamp: String
    let type: String
    let payload: Payload

    struct Payload: Decodable {
        let type: String?
        let rateLimits: RolloutLimits?
        let info: TokenCountInfo?
        let turnId: String?
        let rootTurnId: String?
        let model: String?
        let effort: String?
        let responseId: String?
        let usage: TokenUsage?
    }

    struct RolloutLimits: Decodable {
        let limitId: String?
        let primary: RolloutWindow?
        let secondary: RolloutWindow?
    }

    struct RolloutWindow: Decodable {
        let usedPercent: Double?
        let windowMinutes: Int?
        let resetsAt: Double?
    }

    struct TokenUsage: Decodable {
        let totalTokens: Int?
        let cachedInputTokens: Int?

        var uncachedTokens: Int? {
            totalTokens.map { max(0, $0 - max(0, cachedInputTokens ?? 0)) }
        }
    }

    struct TokenCountInfo: Decodable {
        let totalTokenUsage: TokenUsage?
        let lastTokenUsage: TokenUsage?
    }
}

private struct Turn: Sendable {
    let id: String
    let date: Date
    let model: String
    let effort: String?
}

private struct Usage: Sendable {
    let id: String
    let date: Date
    let tokens: Int
}

private struct Rollout {
    var quotas: [QuotaSample] = []
    var turns: [Turn] = []
    var usage: [Usage] = []
}

private struct FileStamp: Equatable {
    let modifiedAt: Date
    let size: Int
}

private struct CachedRollout {
    let stamp: FileStamp
    let rollout: Rollout
}

private struct CachedCatalog: Decodable {
    let models: [Entry]

    struct Entry: Decodable {
        let slug: String
        let displayName: String
        let visibility: String
        let defaultReasoningLevel: String?
        let supportedReasoningLevels: [Effort]?
        let serviceTiers: [ServiceTier]?
        let additionalSpeedTiers: [String]?
    }

    struct ServiceTier: Decodable {
        let id: String?
    }

    struct Effort: Decodable {
        let effort: String
    }
}

private final class TelemetryReader: @unchecked Sendable {
    static let shared = TelemetryReader()

    private let lock = NSLock()
    private var files: [URL: CachedRollout] = [:]
    private var catalog: (stamp: FileStamp, models: [ModelOption])?
    private var live: LiveTelemetry?
    private var lastLiveAttempt = Date.distantPast
    private let fractional = ISO8601DateFormatter()
    private let seconds = ISO8601DateFormatter()
    private let lineMarkers = ["\"token_count\"", "\"turn_context\"", "\"token_usage_record\""].map { Data($0.utf8) }
    private let decoder = JSONDecoder()
    private let home: URL
    private let fileManager = FileManager.default

    private init() {
        let override = ProcessInfo.processInfo.environment["CODEX_HOME"]
        home = override.map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        decoder.keyDecodingStrategy = .convertFromSnakeCase
    }

    func read(now: Date, force: Bool) -> Telemetry {
        lock.lock()
        defer { lock.unlock() }

        let current = Date()
        let refreshInterval: TimeInterval = force ? 15 : 110
        if current.timeIntervalSince(lastLiveAttempt) >= refreshInterval {
            lastLiveAttempt = current
            if let binary = codexBinary() {
                live = AppServerRead.fetch(binary: binary)
            }
        }

        let rollouts = recentRollouts(now: now)
        let localQuotas = rollouts.flatMap(\.quotas).filter { $0.observedAt <= now }
        let liveQuotas = live?.quotas ?? []
        let liveWeekly = latest(in: liveQuotas, now: now, weekly: true)
        let localWeekly = latest(in: localQuotas, now: now, weekly: true)
        // The account request can outlast the caller's refresh timestamp.
        let quota = liveWeekly.flatMap { sample in
            localWeekly.map { $0.observedAt > sample.observedAt ? $0 : sample } ?? sample
        } ?? localWeekly
        let source = quota == liveWeekly && quota != nil ? "Codex account" : "Local Codex history"
        let shortQuota = latest(in: localQuotas + liveQuotas, now: now, weekly: false)
        let history = quota.map { weekly in
            paceHistory(from: localQuotas + liveQuotas, matching: weekly)
        } ?? []
        let models = live?.models ?? []
        let error = quota == nil
            ? "No current weekly limit has been observed. It will appear after Codex reports your account usage."
            : nil
        return Telemetry(
            quota: quota, shortQuota: shortQuota, history: history,
            activity: recentActivity(in: rollouts, now: now),
            models: models.isEmpty ? modelsFromDisk() : ModelOption.enrichFastSupport(models, from: modelsFromDisk()),
            source: source, error: error
        )
    }

    private func latest(in samples: [QuotaSample], now: Date, weekly: Bool) -> QuotaSample? {
        samples
            .filter { ($0.windowMinutes == weeklyMinutes) == weekly && $0.resetsAt > now }
            .max { $0.observedAt < $1.observedAt }
    }

    private func paceHistory(from samples: [QuotaSample], matching quota: QuotaSample) -> [QuotaSample] {
        let points = samples.filter {
            $0.windowMinutes == weeklyMinutes && $0.resetsAt == quota.resetsAt
                && $0.observedAt <= quota.observedAt
        }.sorted { $0.observedAt < $1.observedAt }
        var recent: [QuotaSample] = []
        for point in points {
            if let last = recent.last, point.observedAt == last.observedAt {
                recent[recent.count - 1] = point
                continue
            }
            recent.append(point)
        }
        return recent
    }

    private func recentActivity(in rollouts: [Rollout], now: Date) -> Activity? {
        let cutoff = now.addingTimeInterval(-3600)
        let turns = rollouts.flatMap(\.turns).filter { $0.date <= now }
        guard let last = turns.max(by: { $0.date < $1.date }) else { return nil }
        let active = Set(turns.lazy.filter { $0.date >= cutoff }.map(\.id))
        var seen = Set<String>()
        var tokens = 0
        for usage in rollouts.flatMap(\.usage) where usage.date >= cutoff && usage.date <= now {
            if seen.insert(usage.id).inserted {
                tokens += usage.tokens
            }
        }
        return Activity(observedAt: last.date, model: last.model, effort: last.effort, turns: active.count, tokens: tokens)
    }

    private func recentRollouts(now: Date) -> [Rollout] {
        let candidates = rolloutFiles(now: now)
        var nextFiles: [URL: CachedRollout] = [:]
        var bytesRemaining = 48 * 1024 * 1024
        var rollouts: [Rollout] = []
        for (url, stamp) in candidates.prefix(96) {
            if let cached = files[url], cached.stamp == stamp {
                nextFiles[url] = cached
                rollouts.append(cached.rollout)
                continue
            }
            let size = min(stamp.size, 3 * 1024 * 1024)
            guard size <= bytesRemaining else {
                if let cached = files[url] {
                    nextFiles[url] = cached
                    rollouts.append(cached.rollout)
                }
                continue
            }
            bytesRemaining -= size
            if let rollout = parseRollout(url: url, size: size, offset: stamp.size - size) {
                nextFiles[url] = CachedRollout(stamp: stamp, rollout: rollout)
                rollouts.append(rollout)
            }
        }
        files = nextFiles
        return rollouts
    }

    private func rolloutFiles(now: Date) -> [(URL, FileStamp)] {
        let properties: [URLResourceKey] = [.contentModificationDateKey, .fileSizeKey, .isRegularFileKey]
        let sessions = home.appendingPathComponent("sessions")
        var folders = [home.appendingPathComponent("archived_sessions")]
        var utc = Calendar(identifier: .gregorian)
        utc.timeZone = TimeZone(secondsFromGMT: 0)!
        for offset in -8...1 {
            guard let date = utc.date(byAdding: .day, value: offset, to: now) else { continue }
            let parts = utc.dateComponents([.year, .month, .day], from: date)
            if let year = parts.year, let month = parts.month, let day = parts.day {
                folders.append(sessions.appendingPathComponent(String(format: "%04d/%02d/%02d", year, month, day)))
            }
        }
        let cutoff = now.addingTimeInterval(-8 * 86400)
        return folders.flatMap { folder in
            (try? fileManager.contentsOfDirectory(at: folder, includingPropertiesForKeys: properties, options: [.skipsHiddenFiles])) ?? []
        }.compactMap { url -> (URL, FileStamp)? in
            guard url.pathExtension == "jsonl", let stamp = fileStamp(url), stamp.modifiedAt >= cutoff else { return nil }
            return (url, stamp)
        }.sorted { $0.1.modifiedAt > $1.1.modifiedAt }
    }

    private func fileStamp(_ url: URL) -> FileStamp? {
        guard let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey, .isRegularFileKey]),
              values.isRegularFile == true,
              let modified = values.contentModificationDate, let size = values.fileSize else { return nil }
        return FileStamp(modifiedAt: modified, size: size)
    }

    private func parseRollout(url: URL, size: Int, offset: Int) -> Rollout? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let buffer = try? readTail(handle: handle, size: size, offset: offset) else { return nil }
        var rollout = Rollout()
        var olderUsage: [Usage] = []
        var lastCumulative: Int?
        for line in buffer.split(separator: 0x0a) {
            guard lineMarkers.contains(where: { line.range(of: $0) != nil }),
                  let record = try? decoder.decode(RolloutRecord.self, from: line),
                  let date = fractional.date(from: record.timestamp) ?? seconds.date(from: record.timestamp) else { continue }
            let payload = record.payload
            switch record.type {
            case "event_msg" where payload.type == "token_count":
                if let limits = payload.rateLimits, limits.limitId == nil || limits.limitId == "codex" {
                    for window in [limits.primary, limits.secondary].compactMap({ $0 }) {
                        if let sample = makeSample(date: date, percent: window.usedPercent, minutes: window.windowMinutes, resets: window.resetsAt) {
                            rollout.quotas.append(sample)
                        }
                    }
                }
                if let total = payload.info?.totalTokenUsage?.uncachedTokens {
                    let delta = lastCumulative.map { total >= $0 ? total - $0 : total }
                        ?? payload.info?.lastTokenUsage?.uncachedTokens ?? 0
                    lastCumulative = total
                    if delta > 0 {
                        olderUsage.append(Usage(id: "\(url.lastPathComponent):\(record.timestamp)", date: date, tokens: delta))
                    }
                }
            case "turn_context":
                if let id = payload.turnId ?? payload.rootTurnId, let model = payload.model,
                   model != "codex-auto-review", payload.rootTurnId == nil || payload.rootTurnId == id {
                    rollout.turns.append(Turn(id: id, date: date, model: model, effort: payload.effort))
                }
            case "token_usage_record":
                if let id = payload.responseId, let tokens = payload.usage?.uncachedTokens, tokens > 0 {
                    rollout.usage.append(Usage(id: id, date: date, tokens: tokens))
                }
            default:
                break
            }
        }
        if rollout.usage.isEmpty {
            rollout.usage = olderUsage
        }
        return rollout
    }

    private func readTail(handle: FileHandle, size: Int, offset: Int) throws -> Data {
        try handle.seek(toOffset: UInt64(offset))
        let data = try handle.read(upToCount: size) ?? Data()
        guard offset > 0 else { return data }
        guard let newline = data.firstIndex(of: 0x0a) else { return Data() }
        return data.suffix(from: data.index(after: newline))
    }

    private func modelsFromDisk() -> [ModelOption] {
        let url = home.appendingPathComponent("models_cache.json")
        guard let stamp = fileStamp(url), stamp.size <= 10 * 1024 * 1024 else { return [] }
        if let catalog, catalog.stamp == stamp { return catalog.models }
        guard let data = try? Data(contentsOf: url),
              let entries = try? decoder.decode(CachedCatalog.self, from: data).models else { return [] }
        let models = entries.filter { $0.visibility == "list" }.enumerated().map { index, model in
            ModelOption(
                id: model.slug, name: model.displayName,
                efforts: model.supportedReasoningLevels?.map(\.effort) ?? [],
                defaultEffort: model.defaultReasoningLevel, isDefault: index == 0,
                supportsFastMode: ModelOption.fastModeSupport(
                    serviceTierIDs: model.serviceTiers?.compactMap(\.id),
                    additionalSpeedTiers: model.additionalSpeedTiers
                )
            )
        }
        catalog = (stamp, models)
        return models
    }

    private func codexBinary() -> URL? {
        let environment = ProcessInfo.processInfo.environment
        let paths = [
            environment["CODEX_TICKER_CODEX"],
            environment["CODEX_PACE_CODEX"],
            "/Applications/ChatGPT.app/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex",
            "/Applications/Codex.app/Contents/Resources/codex",
            "/opt/homebrew/bin/codex",
            "/usr/local/bin/codex",
        ].compactMap { $0 } + (environment["PATH"] ?? "").split(separator: ":").map { "\($0)/codex" }
        return paths.first(where: fileManager.isExecutableFile(atPath:)).map { URL(fileURLWithPath: $0) }
    }
}

private func makeSample(date: Date, percent: Double?, minutes: Int?, resets: Double?) -> QuotaSample? {
    guard let percent, (0...100).contains(percent),
          let minutes, (1...weeklyMinutes).contains(minutes),
          let resets, resets.isFinite, resets > 0 else { return nil }
    return QuotaSample(
        observedAt: date, resetsAt: Date(timeIntervalSince1970: resets),
        windowMinutes: minutes, usedPercent: percent
    )
}

private struct LiveTelemetry {
    let quotas: [QuotaSample]
    let models: [ModelOption]
}

/// Short-lived stdio client for the two read-only Codex App Server requests.
private final class AppServerRead: @unchecked Sendable {
    private let writer: FileHandle
    private let lock = NSLock()
    private let done = DispatchSemaphore(value: 0)
    private var buffer = Data()
    private var quotas: [QuotaSample] = []
    private var models: [ModelOption] = []
    private var outstanding: Set<Int> = [2, 3]
    private var active = true

    private init(writer: FileHandle) {
        self.writer = writer
        _ = fcntl(writer.fileDescriptor, F_SETNOSIGPIPE, 1)
    }

    static func fetch(binary: URL) -> LiveTelemetry? {
        let process = Process()
        let input = Pipe()
        let output = Pipe()
        let client = AppServerRead(writer: input.fileHandleForWriting)
        process.executableURL = binary
        process.arguments = ["app-server", "--stdio"]
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        output.fileHandleForReading.readabilityHandler = { handle in
            client.receive(handle.availableData)
        }
        process.terminationHandler = { _ in client.done.signal() }

        do {
            try process.run()
        } catch {
            output.fileHandleForReading.readabilityHandler = nil
            return nil
        }
        client.start()
        _ = client.done.wait(timeout: .now() + .seconds(4))
        let result = client.finish()
        output.fileHandleForReading.readabilityHandler = nil
        if process.isRunning { process.terminate() }
        return result
    }

    private func start() {
        lock.lock()
        defer { lock.unlock() }
        send(["id": 1, "method": "initialize", "params": [
            "clientInfo": ["name": "codex_ticker", "title": "Codex Ticker", "version": "1.0.0"]
        ]])
    }

    private func receive(_ data: Data) {
        lock.lock()
        defer { lock.unlock() }
        guard active, !data.isEmpty else { return }
        buffer.append(data)
        guard buffer.count <= 8 * 1024 * 1024 else {
            done.signal()
            return
        }
        while let end = buffer.firstIndex(of: 0x0a) {
            let line = buffer.prefix(upTo: end)
            buffer.removeSubrange(...end)
            guard let message = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                  let id = message["id"] as? Int else { continue }
            let result = message["result"] as? [String: Any]
            switch id {
            case 1:
                guard result != nil else {
                    done.signal()
                    return
                }
                send(["method": "initialized"])
                send(["id": 2, "method": "account/rateLimits/read"])
                send(["id": 3, "method": "model/list", "params": ["includeHidden": false]])
            case 2, 3:
                if let result {
                    if id == 2 { quotas = parseQuotas(result) }
                    if id == 3 { models = parseModels(result) }
                }
                outstanding.remove(id)
                if outstanding.isEmpty { done.signal() }
            default:
                break
            }
        }
    }

    private func finish() -> LiveTelemetry? {
        lock.lock()
        defer { lock.unlock() }
        active = false
        try? writer.close()
        guard !quotas.isEmpty || !models.isEmpty else { return nil }
        return LiveTelemetry(quotas: quotas, models: models)
    }

    private func send(_ message: [String: Any]) {
        guard var data = try? JSONSerialization.data(withJSONObject: message) else { return }
        data.append(0x0a)
        do {
            try writer.write(contentsOf: data)
        } catch {
            done.signal()
        }
    }

    private func parseQuotas(_ response: [String: Any]) -> [QuotaSample] {
        let map = response["rateLimitsByLimitId"] as? [String: [String: Any]]
        guard let snapshot = map?["codex"] ?? response["rateLimits"] as? [String: Any],
              snapshot["limitId"] as? String == nil || snapshot["limitId"] as? String == "codex" else { return [] }
        let date = Date()
        return ["primary", "secondary"].compactMap { key in
            guard let window = snapshot[key] as? [String: Any] else { return nil }
            return makeSample(
                date: date, percent: window["usedPercent"] as? Double,
                minutes: window["windowDurationMins"] as? Int, resets: window["resetsAt"] as? Double
            )
        }
    }

    private func parseModels(_ response: [String: Any]) -> [ModelOption] {
        let entries = response["data"] as? [[String: Any]] ?? []
        return entries.compactMap { model in
            guard let id = (model["model"] ?? model["id"]) as? String,
                  model["hidden"] as? Bool != true else { return nil }
            let efforts = (model["supportedReasoningEfforts"] as? [[String: Any]])?.compactMap {
                $0["reasoningEffort"] as? String
            } ?? []
            return ModelOption(
                id: id, name: model["displayName"] as? String ?? id, efforts: efforts,
                defaultEffort: model["defaultReasoningEffort"] as? String,
                isDefault: model["isDefault"] as? Bool ?? false,
                supportsFastMode: ModelOption.fastModeSupport(
                    serviceTierIDs: (model["serviceTiers"] as? [[String: Any]])?.compactMap { $0["id"] as? String },
                    additionalSpeedTiers: model["additionalSpeedTiers"] as? [String]
                )
            )
        }
    }
}
