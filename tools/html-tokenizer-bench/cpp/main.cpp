#include <algorithm>
#include <cassert>
#include <chrono>
#include <cctype>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace {

constexpr std::uint64_t kFnvOffsetBasis = 14695981039346656037ULL;
constexpr std::uint64_t kFnvPrime = 1099511628211ULL;

struct TokenizerResult {
    std::uint64_t token_count;
    std::uint64_t checksum;

    bool operator==(const TokenizerResult&) const = default;
};

struct Options {
    std::filesystem::path input_path;
    std::size_t iterations = 7;
    std::size_t warmup_iterations = 2;
};

class TokenHash {
public:
    void AddByte(std::uint8_t value) {
        value_ ^= value;
        value_ *= kFnvPrime;
    }

    void AddUInt64(std::uint64_t value) {
        for (int index = 0; index < 8; ++index) {
            AddByte(static_cast<std::uint8_t>(value >> (index * 8)));
        }
    }

    void AddAscii(std::string_view value) {
        AddUInt64(value.size());
        for (unsigned char character : value) {
            if (character > 0x7f) {
                throw std::runtime_error(
                    "tokenizer emitted non-ASCII output for the ASCII benchmark corpus");
            }
            AddByte(character);
        }
    }

    [[nodiscard]] std::uint64_t Value() const {
        return value_;
    }

private:
    std::uint64_t value_ = kFnvOffsetBasis;
};

struct Attribute {
    std::string name;
    std::string value;
};

[[nodiscard]] bool IsAsciiWhitespace(char value) {
    return value == '\t' || value == '\n' || value == '\f' ||
           value == '\r' || value == ' ';
}

[[nodiscard]] bool IsAsciiAlpha(char value) {
    return (value >= 'a' && value <= 'z') ||
           (value >= 'A' && value <= 'Z');
}

[[nodiscard]] bool IsAsciiDigit(char value) {
    return value >= '0' && value <= '9';
}

[[nodiscard]] char AsciiLower(char value) {
    return value >= 'A' && value <= 'Z'
        ? static_cast<char>(value + ('a' - 'A'))
        : value;
}

[[nodiscard]] std::string AsciiLowerString(std::string_view value) {
    std::string output;
    output.reserve(value.size());
    for (char character : value) {
        output.push_back(AsciiLower(character));
    }
    return output;
}

[[nodiscard]] bool EqualsAsciiCaseInsensitive(
    std::string_view left,
    std::string_view right) {
    if (left.size() != right.size()) {
        return false;
    }
    for (std::size_t index = 0; index < left.size(); ++index) {
        if (AsciiLower(left[index]) != AsciiLower(right[index])) {
            return false;
        }
    }
    return true;
}

[[nodiscard]] std::optional<std::uint32_t> HexValue(char value) {
    if (value >= '0' && value <= '9') {
        return static_cast<std::uint32_t>(value - '0');
    }
    if (value >= 'a' && value <= 'f') {
        return static_cast<std::uint32_t>(value - 'a' + 10);
    }
    if (value >= 'A' && value <= 'F') {
        return static_cast<std::uint32_t>(value - 'A' + 10);
    }
    return std::nullopt;
}

[[nodiscard]] std::pair<std::string, std::size_t> ParseCharacterReference(
    std::string_view input,
    std::size_t position) {
    const std::string_view remaining = input.substr(position);
    constexpr std::pair<std::string_view, std::string_view> references[] = {
        {"&quot;", "\""},
        {"&apos;", "'"},
        {"&amp;", "&"},
        {"&lt;", "<"},
        {"&gt;", ">"},
    };
    for (const auto& [encoded, decoded] : references) {
        if (remaining.starts_with(encoded)) {
            return {std::string(decoded), encoded.size()};
        }
    }

    if (remaining.starts_with("&#")) {
        std::size_t index = 2;
        const bool hexadecimal =
            index < remaining.size() &&
            (remaining[index] == 'x' || remaining[index] == 'X');
        if (hexadecimal) {
            ++index;
        }

        const std::size_t digit_start = index;
        std::uint32_t value = 0;
        while (index < remaining.size()) {
            std::optional<std::uint32_t> digit;
            if (hexadecimal) {
                digit = HexValue(remaining[index]);
            } else if (IsAsciiDigit(remaining[index])) {
                digit = static_cast<std::uint32_t>(remaining[index] - '0');
            }
            if (!digit.has_value()) {
                break;
            }
            value = value * (hexadecimal ? 16u : 10u) + digit.value();
            ++index;
        }

        if (index > digit_start) {
            if (index < remaining.size() && remaining[index] == ';') {
                ++index;
            }
            if (value <= 0x7f) {
                return {
                    std::string(1, static_cast<char>(value)),
                    index,
                };
            }
        }
    }

    return {"&", 1};
}

[[nodiscard]] std::string DecodeCharacterReferences(std::string_view input) {
    std::string output;
    output.reserve(input.size());
    std::size_t position = 0;
    while (position < input.size()) {
        if (input[position] == '&') {
            auto [value, consumed] = ParseCharacterReference(input, position);
            output.append(value);
            position += consumed;
        } else {
            output.push_back(input[position]);
            ++position;
        }
    }
    return output;
}

class Tokenizer {
public:
    explicit Tokenizer(std::string_view input)
        : input_(input) {}

    [[nodiscard]] TokenizerResult Run() {
        while (position_ < input_.size()) {
            switch (input_[position_]) {
                case '&': {
                    auto [value, consumed] =
                        ParseCharacterReference(input_, position_);
                    AdvanceTo(position_ + consumed);
                    EmitCharacter(std::move(value));
                    break;
                }
                case '<':
                    ConsumeLessThan();
                    break;
                default: {
                    const std::size_t start = position_;
                    std::size_t end = start;
                    while (end < input_.size() &&
                           input_[end] != '&' &&
                           input_[end] != '<') {
                        ++end;
                    }
                    std::string value(input_.substr(start, end - start));
                    AdvanceTo(end);
                    EmitCharacter(std::move(value));
                    break;
                }
            }
        }

        EmitType(5);
        return {token_count_, hash_.Value()};
    }

private:
    void ConsumeLessThan() {
        Advance();
        if (position_ >= input_.size()) {
            EmitCharacter("<");
            return;
        }

        if (StartsWith("!--")) {
            ConsumeComment();
        } else if (StartsWithAsciiCaseInsensitive("!doctype")) {
            ConsumeDoctype();
        } else if (input_[position_] == '/') {
            ConsumeEndTag();
        } else if (IsAsciiAlpha(input_[position_])) {
            ConsumeStartTag();
        } else {
            EmitCharacter("<");
        }
    }

    void ConsumeComment() {
        AdvanceTo(position_ + 3);
        const std::size_t data_start = position_;
        const std::size_t end = input_.find("-->", data_start);
        std::string data;
        if (end == std::string_view::npos) {
            data = std::string(input_.substr(data_start));
            AdvanceTo(input_.size());
        } else {
            data = std::string(input_.substr(data_start, end - data_start));
            AdvanceTo(end + 3);
        }

        EmitType(3);
        hash_.AddAscii(data);
    }

    void ConsumeDoctype() {
        AdvanceTo(position_ + 8);
        SkipWhitespace();
        const std::size_t name_start = position_;
        while (position_ < input_.size() &&
               !IsAsciiWhitespace(input_[position_]) &&
               input_[position_] != '>') {
            Advance();
        }
        const std::string name =
            AsciiLowerString(input_.substr(name_start, position_ - name_start));
        while (position_ < input_.size() && input_[position_] != '>') {
            Advance();
        }
        if (position_ < input_.size()) {
            Advance();
        }

        EmitType(0);
        hash_.AddAscii(name);
        hash_.AddAscii("");
        hash_.AddAscii("");
        hash_.AddByte(0);
    }

    void ConsumeEndTag() {
        Advance();
        const std::size_t name_start = position_;
        while (position_ < input_.size() &&
               IsTagNameByte(input_[position_])) {
            Advance();
        }
        const std::string name =
            AsciiLowerString(input_.substr(name_start, position_ - name_start));
        while (position_ < input_.size() && input_[position_] != '>') {
            Advance();
        }
        if (position_ < input_.size()) {
            Advance();
        }

        EmitTag(2, name, false, {});
    }

    void ConsumeStartTag() {
        const std::size_t name_start = position_;
        while (position_ < input_.size() &&
               IsTagNameByte(input_[position_])) {
            Advance();
        }
        const std::string name =
            AsciiLowerString(input_.substr(name_start, position_ - name_start));
        std::vector<Attribute> attributes;
        bool self_closing = false;

        while (true) {
            SkipWhitespace();
            if (position_ >= input_.size()) {
                break;
            }

            if (input_[position_] == '>') {
                Advance();
                break;
            }

            if (input_[position_] == '/' &&
                position_ + 1 < input_.size() &&
                input_[position_ + 1] == '>') {
                AdvanceTo(position_ + 2);
                self_closing = true;
                break;
            }

            const std::size_t attribute_name_start = position_;
            while (position_ < input_.size() &&
                   !IsAsciiWhitespace(input_[position_]) &&
                   input_[position_] != '=' &&
                   input_[position_] != '/' &&
                   input_[position_] != '>') {
                Advance();
            }

            if (attribute_name_start == position_) {
                Advance();
                continue;
            }

            std::string attribute_name = AsciiLowerString(
                input_.substr(
                    attribute_name_start,
                    position_ - attribute_name_start));
            SkipWhitespace();
            std::string attribute_value;
            if (position_ < input_.size() && input_[position_] == '=') {
                Advance();
                SkipWhitespace();
                attribute_value = ConsumeAttributeValue();
            }

            const bool duplicate = std::any_of(
                attributes.begin(),
                attributes.end(),
                [&](const Attribute& existing) {
                    return EqualsAsciiCaseInsensitive(
                        existing.name,
                        attribute_name);
                });
            if (!duplicate) {
                attributes.push_back({
                    std::move(attribute_name),
                    std::move(attribute_value),
                });
            }
        }

        EmitTag(1, name, self_closing, attributes);
    }

    [[nodiscard]] std::string ConsumeAttributeValue() {
        if (position_ >= input_.size()) {
            return {};
        }

        const char quote = input_[position_];
        if (quote == '\'' || quote == '"') {
            Advance();
            const std::size_t start = position_;
            while (position_ < input_.size() && input_[position_] != quote) {
                Advance();
            }
            std::string value = DecodeCharacterReferences(
                input_.substr(start, position_ - start));
            if (position_ < input_.size()) {
                Advance();
            }
            return value;
        }

        const std::size_t start = position_;
        while (position_ < input_.size() &&
               !IsAsciiWhitespace(input_[position_]) &&
               input_[position_] != '>') {
            Advance();
        }
        return DecodeCharacterReferences(
            input_.substr(start, position_ - start));
    }

    void EmitTag(
        std::uint8_t token_type,
        std::string_view name,
        bool self_closing,
        const std::vector<Attribute>& attributes) {
        EmitType(token_type);
        hash_.AddAscii(name);
        hash_.AddByte(self_closing ? 1 : 0);
        hash_.AddUInt64(attributes.size());
        for (const Attribute& attribute : attributes) {
            hash_.AddAscii(attribute.name);
            hash_.AddAscii(attribute.value);
        }
    }

    void EmitCharacter(std::string value) {
        EmitType(4);
        hash_.AddAscii(value);
    }

    void EmitType(std::uint8_t token_type) {
        hash_.AddByte(token_type);
        ++token_count_;
    }

    [[nodiscard]] bool StartsWith(std::string_view value) const {
        return input_.substr(position_).starts_with(value);
    }

    [[nodiscard]] bool StartsWithAsciiCaseInsensitive(
        std::string_view value) const {
        if (position_ + value.size() > input_.size()) {
            return false;
        }
        return EqualsAsciiCaseInsensitive(
            input_.substr(position_, value.size()),
            value);
    }

    void SkipWhitespace() {
        while (position_ < input_.size() &&
               IsAsciiWhitespace(input_[position_])) {
            Advance();
        }
    }

    void Advance() {
        AdvanceTo(position_ + 1);
    }

    void AdvanceTo(std::size_t target) {
        target = std::min(target, input_.size());
        while (position_ < target) {
            const char value = input_[position_++];
            if (value == '\r') {
                ++line_;
                column_ = 1;
                previous_was_carriage_return_ = true;
            } else if (value == '\n') {
                if (!previous_was_carriage_return_) {
                    ++line_;
                    column_ = 1;
                }
                previous_was_carriage_return_ = false;
            } else {
                ++column_;
                previous_was_carriage_return_ = false;
            }
        }
    }

    [[nodiscard]] static bool IsTagNameByte(char value) {
        return !IsAsciiWhitespace(value) && value != '/' && value != '>';
    }

    std::string_view input_;
    std::size_t position_ = 0;
    std::size_t line_ = 1;
    std::size_t column_ = 1;
    bool previous_was_carriage_return_ = false;
    TokenHash hash_;
    std::uint64_t token_count_ = 0;
};

[[nodiscard]] std::size_t ParseCount(
    std::string_view value,
    std::string_view option) {
    std::size_t parsed_characters = 0;
    std::size_t parsed = 0;
    try {
        parsed = std::stoull(std::string(value), &parsed_characters);
    } catch (const std::exception&) {
        throw std::invalid_argument(
            std::string(option) + " must be a non-negative integer");
    }
    if (parsed_characters != value.size()) {
        throw std::invalid_argument(
            std::string(option) + " must be a non-negative integer");
    }
    return parsed;
}

[[nodiscard]] Options ParseOptions(int argc, char** argv) {
    Options options;
    for (int index = 1; index < argc; ++index) {
        const std::string_view argument(argv[index]);
        auto read_value = [&]() -> std::string_view {
            if (++index >= argc) {
                throw std::invalid_argument(
                    std::string(argument) + " requires a value");
            }
            return argv[index];
        };

        if (argument == "--input") {
            options.input_path = read_value();
        } else if (argument == "--iterations") {
            options.iterations = ParseCount(read_value(), argument);
        } else if (argument == "--warmup") {
            options.warmup_iterations = ParseCount(read_value(), argument);
        } else {
            throw std::invalid_argument(
                "unknown argument '" + std::string(argument) + "'");
        }
    }

    if (options.input_path.empty()) {
        throw std::invalid_argument("--input is required");
    }
    if (options.iterations == 0) {
        throw std::invalid_argument(
            "at least one measured iteration is required");
    }
    return options;
}

[[nodiscard]] std::string ReadFile(const std::filesystem::path& path) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) {
        throw std::runtime_error("failed to read benchmark corpus");
    }
    return {
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>(),
    };
}

}  // namespace

int main(int argc, char** argv) {
    try {
        const Options options = ParseOptions(argc, argv);
        const std::string input = ReadFile(options.input_path);
        if (std::any_of(
                input.begin(),
                input.end(),
                [](unsigned char value) { return value > 0x7f; })) {
            throw std::runtime_error(
                "the cross-language benchmark corpus must remain ASCII");
        }

        for (std::size_t iteration = 0;
             iteration < options.warmup_iterations;
             ++iteration) {
            volatile TokenizerResult warmup = Tokenizer(input).Run();
            (void)warmup;
        }

        std::vector<double> elapsed_ms;
        elapsed_ms.reserve(options.iterations);
        std::optional<TokenizerResult> expected;
        for (std::size_t iteration = 0;
             iteration < options.iterations;
             ++iteration) {
            const auto started = std::chrono::steady_clock::now();
            const TokenizerResult result = Tokenizer(input).Run();
            const auto finished = std::chrono::steady_clock::now();
            elapsed_ms.push_back(
                std::chrono::duration<double, std::milli>(
                    finished - started).count());

            if (expected.has_value() && result != expected.value()) {
                throw std::runtime_error(
                    "tokenizer output changed between benchmark iterations");
            }
            expected = result;
        }

        std::sort(elapsed_ms.begin(), elapsed_ms.end());
        const double median_ms = elapsed_ms[elapsed_ms.size() / 2];
        const double throughput =
            (static_cast<double>(input.size()) / (1024.0 * 1024.0)) /
            (median_ms / 1000.0);

        std::ostringstream checksum;
        checksum << std::hex << std::setfill('0') << std::setw(16)
                 << expected->checksum;

        std::cout << std::fixed
                  << "{"
                  << "\"benchmark_id\":\"cpp-common\","
                  << "\"language\":\"cpp\","
                  << "\"implementation\":\"benchmark common-state port\","
                  << "\"variant\":\"common-state-port\","
                  << "\"input_bytes\":" << input.size() << ","
                  << "\"iterations\":" << options.iterations << ","
                  << "\"warmup_iterations\":"
                  << options.warmup_iterations << ","
                  << "\"token_count\":" << expected->token_count << ","
                  << "\"checksum\":\"" << checksum.str() << "\","
                  << "\"median_ms\":" << std::setprecision(6)
                  << median_ms << ","
                  << "\"min_ms\":" << elapsed_ms.front() << ","
                  << "\"max_ms\":" << elapsed_ms.back() << ","
                  << "\"throughput_mib_per_second\":"
                  << std::setprecision(3) << throughput << ","
                  << "\"allocated_bytes_per_iteration\":null"
                  << "}"
                  << '\n';
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
