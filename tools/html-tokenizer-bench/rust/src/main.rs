use std::env;
use std::fs;
use std::hint::black_box;
use std::path::PathBuf;
use std::time::Instant;

const FNV_OFFSET_BASIS: u64 = 14_695_981_039_346_656_037;
const FNV_PRIME: u64 = 1_099_511_628_211;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct TokenizerResult {
    token_count: u64,
    checksum: u64,
}

struct Options {
    input_path: PathBuf,
    iterations: usize,
    warmup_iterations: usize,
}

fn main() {
    let options = Options::parse();
    let input = fs::read(&options.input_path).expect("failed to read benchmark corpus");
    assert!(
        input.iter().all(u8::is_ascii),
        "the cross-language benchmark corpus must remain ASCII"
    );

    for _ in 0..options.warmup_iterations {
        black_box(tokenize(&input));
    }

    let mut elapsed_ms = Vec::with_capacity(options.iterations);
    let mut expected: Option<TokenizerResult> = None;

    for _ in 0..options.iterations {
        let started = Instant::now();
        let result = black_box(tokenize(black_box(&input)));
        elapsed_ms.push(started.elapsed().as_secs_f64() * 1000.0);

        if let Some(expected_result) = expected {
            assert_eq!(
                result, expected_result,
                "tokenizer output changed between benchmark iterations"
            );
        } else {
            expected = Some(result);
        }
    }

    elapsed_ms.sort_by(f64::total_cmp);
    let median_ms = elapsed_ms[elapsed_ms.len() / 2];
    let throughput = (input.len() as f64 / (1024.0 * 1024.0)) / (median_ms / 1000.0);
    let result = expected.expect("at least one measured iteration is required");

    println!(
        concat!(
            "{{",
            "\"benchmark_id\":\"rust-common\",",
            "\"language\":\"rust\",",
            "\"implementation\":\"benchmark common-state port\",",
            "\"variant\":\"common-state-port\",",
            "\"input_bytes\":{},",
            "\"iterations\":{},",
            "\"warmup_iterations\":{},",
            "\"token_count\":{},",
            "\"checksum\":\"{:016x}\",",
            "\"median_ms\":{:.6},",
            "\"min_ms\":{:.6},",
            "\"max_ms\":{:.6},",
            "\"throughput_mib_per_second\":{:.3},",
            "\"allocated_bytes_per_iteration\":null",
            "}}"
        ),
        input.len(),
        options.iterations,
        options.warmup_iterations,
        result.token_count,
        result.checksum,
        median_ms,
        elapsed_ms[0],
        elapsed_ms[elapsed_ms.len() - 1],
        throughput
    );
}

fn tokenize(input: &[u8]) -> TokenizerResult {
    let mut tokenizer = Tokenizer::new(input);
    tokenizer.run();
    TokenizerResult {
        token_count: tokenizer.token_count,
        checksum: tokenizer.hash.value,
    }
}

struct TokenHash {
    value: u64,
}

impl TokenHash {
    fn new() -> Self {
        Self {
            value: FNV_OFFSET_BASIS,
        }
    }

    fn add_byte(&mut self, value: u8) {
        self.value ^= u64::from(value);
        self.value = self.value.wrapping_mul(FNV_PRIME);
    }

    fn add_u64(&mut self, value: u64) {
        for byte in value.to_le_bytes() {
            self.add_byte(byte);
        }
    }

    fn add_ascii(&mut self, value: &str) {
        assert!(
            value.is_ascii(),
            "tokenizer emitted non-ASCII output for the ASCII benchmark corpus"
        );
        self.add_u64(value.len() as u64);
        for byte in value.bytes() {
            self.add_byte(byte);
        }
    }
}

struct Attribute {
    name: String,
    value: String,
}

struct Tokenizer<'a> {
    input: &'a [u8],
    position: usize,
    line: usize,
    column: usize,
    previous_was_carriage_return: bool,
    hash: TokenHash,
    token_count: u64,
}

impl<'a> Tokenizer<'a> {
    fn new(input: &'a [u8]) -> Self {
        Self {
            input,
            position: 0,
            line: 1,
            column: 1,
            previous_was_carriage_return: false,
            hash: TokenHash::new(),
            token_count: 0,
        }
    }

    fn run(&mut self) {
        while self.position < self.input.len() {
            match self.input[self.position] {
                b'&' => {
                    let (value, consumed) = parse_character_reference(self.input, self.position);
                    self.advance_to(self.position + consumed);
                    self.emit_character(value);
                }
                b'<' => self.consume_less_than(),
                _ => {
                    let start = self.position;
                    let mut end = start;
                    while end < self.input.len() && !matches!(self.input[end], b'&' | b'<') {
                        end += 1;
                    }
                    let value = ascii_string(&self.input[start..end]);
                    self.advance_to(end);
                    self.emit_character(value);
                }
            }
        }

        self.emit_type(5);
    }

    fn consume_less_than(&mut self) {
        self.advance();
        if self.position >= self.input.len() {
            self.emit_character("<".to_owned());
            return;
        }

        if self.starts_with(b"!--") {
            self.consume_comment();
        } else if self.starts_with_ascii_case_insensitive(b"!doctype") {
            self.consume_doctype();
        } else if self.input[self.position] == b'/' {
            self.consume_end_tag();
        } else if self.input[self.position].is_ascii_alphabetic() {
            self.consume_start_tag();
        } else {
            self.emit_character("<".to_owned());
        }
    }

    fn consume_comment(&mut self) {
        self.advance_to(self.position + 3);
        let data_start = self.position;
        let mut end = data_start;
        while end + 2 < self.input.len() && &self.input[end..end + 3] != b"-->" {
            end += 1;
        }

        let data = if end + 2 < self.input.len() {
            let value = ascii_string(&self.input[data_start..end]);
            self.advance_to(end + 3);
            value
        } else {
            let value = ascii_string(&self.input[data_start..]);
            self.advance_to(self.input.len());
            value
        };

        self.emit_type(3);
        self.hash.add_ascii(&data);
    }

    fn consume_doctype(&mut self) {
        self.advance_to(self.position + 8);
        self.skip_whitespace();
        let name_start = self.position;
        while self.position < self.input.len()
            && !self.input[self.position].is_ascii_whitespace()
            && self.input[self.position] != b'>'
        {
            self.advance();
        }

        let name = ascii_lower_string(&self.input[name_start..self.position]);
        while self.position < self.input.len() && self.input[self.position] != b'>' {
            self.advance();
        }
        if self.position < self.input.len() {
            self.advance();
        }

        self.emit_type(0);
        self.hash.add_ascii(&name);
        self.hash.add_ascii("");
        self.hash.add_ascii("");
        self.hash.add_byte(0);
    }

    fn consume_end_tag(&mut self) {
        self.advance();
        let name_start = self.position;
        while self.position < self.input.len() && is_tag_name_byte(self.input[self.position]) {
            self.advance();
        }
        let name = ascii_lower_string(&self.input[name_start..self.position]);

        while self.position < self.input.len() && self.input[self.position] != b'>' {
            self.advance();
        }
        if self.position < self.input.len() {
            self.advance();
        }

        self.emit_tag(2, &name, false, &[]);
    }

    fn consume_start_tag(&mut self) {
        let name_start = self.position;
        while self.position < self.input.len() && is_tag_name_byte(self.input[self.position]) {
            self.advance();
        }
        let name = ascii_lower_string(&self.input[name_start..self.position]);
        let mut attributes = Vec::new();
        let mut self_closing = false;

        loop {
            self.skip_whitespace();
            if self.position >= self.input.len() {
                break;
            }

            if self.input[self.position] == b'>' {
                self.advance();
                break;
            }

            if self.input[self.position] == b'/'
                && self.position + 1 < self.input.len()
                && self.input[self.position + 1] == b'>'
            {
                self.advance_to(self.position + 2);
                self_closing = true;
                break;
            }

            let attribute_name_start = self.position;
            while self.position < self.input.len()
                && !self.input[self.position].is_ascii_whitespace()
                && !matches!(self.input[self.position], b'=' | b'/' | b'>')
            {
                self.advance();
            }

            if attribute_name_start == self.position {
                self.advance();
                continue;
            }

            let attribute_name =
                ascii_lower_string(&self.input[attribute_name_start..self.position]);
            self.skip_whitespace();
            let attribute_value =
                if self.position < self.input.len() && self.input[self.position] == b'=' {
                    self.advance();
                    self.skip_whitespace();
                    self.consume_attribute_value()
                } else {
                    String::new()
                };

            if !attributes
                .iter()
                .any(|existing: &Attribute| existing.name.eq_ignore_ascii_case(&attribute_name))
            {
                attributes.push(Attribute {
                    name: attribute_name,
                    value: attribute_value,
                });
            }
        }

        self.emit_tag(1, &name, self_closing, &attributes);
    }

    fn consume_attribute_value(&mut self) -> String {
        if self.position >= self.input.len() {
            return String::new();
        }

        let quote = self.input[self.position];
        if matches!(quote, b'\'' | b'"') {
            self.advance();
            let start = self.position;
            while self.position < self.input.len() && self.input[self.position] != quote {
                self.advance();
            }
            let value = decode_character_references(&self.input[start..self.position]);
            if self.position < self.input.len() {
                self.advance();
            }
            value
        } else {
            let start = self.position;
            while self.position < self.input.len()
                && !self.input[self.position].is_ascii_whitespace()
                && self.input[self.position] != b'>'
            {
                self.advance();
            }
            decode_character_references(&self.input[start..self.position])
        }
    }

    fn emit_tag(
        &mut self,
        token_type: u8,
        name: &str,
        self_closing: bool,
        attributes: &[Attribute],
    ) {
        self.emit_type(token_type);
        self.hash.add_ascii(name);
        self.hash.add_byte(u8::from(self_closing));
        self.hash.add_u64(attributes.len() as u64);
        for attribute in attributes {
            self.hash.add_ascii(&attribute.name);
            self.hash.add_ascii(&attribute.value);
        }
    }

    fn emit_character(&mut self, value: String) {
        self.emit_type(4);
        self.hash.add_ascii(&value);
    }

    fn emit_type(&mut self, token_type: u8) {
        self.hash.add_byte(token_type);
        self.token_count += 1;
    }

    fn starts_with(&self, value: &[u8]) -> bool {
        self.input[self.position..].starts_with(value)
    }

    fn starts_with_ascii_case_insensitive(&self, value: &[u8]) -> bool {
        self.input
            .get(self.position..self.position + value.len())
            .is_some_and(|candidate| candidate.eq_ignore_ascii_case(value))
    }

    fn skip_whitespace(&mut self) {
        while self.position < self.input.len() && self.input[self.position].is_ascii_whitespace() {
            self.advance();
        }
    }

    fn advance(&mut self) {
        self.advance_to(self.position + 1);
    }

    fn advance_to(&mut self, target: usize) {
        let target = target.min(self.input.len());
        while self.position < target {
            let value = self.input[self.position];
            self.position += 1;
            match value {
                b'\r' => {
                    self.line += 1;
                    self.column = 1;
                    self.previous_was_carriage_return = true;
                }
                b'\n' => {
                    if !self.previous_was_carriage_return {
                        self.line += 1;
                        self.column = 1;
                    }
                    self.previous_was_carriage_return = false;
                }
                _ => {
                    self.column += 1;
                    self.previous_was_carriage_return = false;
                }
            }
        }
    }
}

fn parse_character_reference(input: &[u8], position: usize) -> (String, usize) {
    let remaining = &input[position..];
    for (encoded, decoded) in [
        (&b"&quot;"[..], "\""),
        (&b"&apos;"[..], "'"),
        (&b"&amp;"[..], "&"),
        (&b"&lt;"[..], "<"),
        (&b"&gt;"[..], ">"),
    ] {
        if remaining.starts_with(encoded) {
            return (decoded.to_owned(), encoded.len());
        }
    }

    if remaining.starts_with(b"&#") {
        let mut index = 2;
        let hexadecimal = remaining
            .get(index)
            .is_some_and(|value| matches!(value, b'x' | b'X'));
        if hexadecimal {
            index += 1;
        }
        let digit_start = index;
        let mut value = 0u32;
        while let Some(byte) = remaining.get(index).copied() {
            let digit = if hexadecimal {
                hex_value(byte)
            } else if byte.is_ascii_digit() {
                Some(u32::from(byte - b'0'))
            } else {
                None
            };
            let Some(digit) = digit else {
                break;
            };
            value = value
                .saturating_mul(if hexadecimal { 16 } else { 10 })
                .saturating_add(digit);
            index += 1;
        }
        if index > digit_start {
            if remaining.get(index) == Some(&b';') {
                index += 1;
            }
            if let Some(character) = char::from_u32(value).filter(char::is_ascii) {
                return (character.to_string(), index);
            }
        }
    }

    ("&".to_owned(), 1)
}

fn decode_character_references(input: &[u8]) -> String {
    let mut output = String::with_capacity(input.len());
    let mut position = 0;
    while position < input.len() {
        if input[position] == b'&' {
            let (value, consumed) = parse_character_reference(input, position);
            output.push_str(&value);
            position += consumed;
        } else {
            output.push(char::from(input[position]));
            position += 1;
        }
    }
    output
}

fn hex_value(value: u8) -> Option<u32> {
    match value {
        b'0'..=b'9' => Some(u32::from(value - b'0')),
        b'a'..=b'f' => Some(u32::from(value - b'a' + 10)),
        b'A'..=b'F' => Some(u32::from(value - b'A' + 10)),
        _ => None,
    }
}

fn is_tag_name_byte(value: u8) -> bool {
    !value.is_ascii_whitespace() && !matches!(value, b'/' | b'>')
}

fn ascii_string(value: &[u8]) -> String {
    String::from_utf8(value.to_vec()).expect("benchmark corpus must remain ASCII")
}

fn ascii_lower_string(value: &[u8]) -> String {
    value
        .iter()
        .map(|byte| char::from(byte.to_ascii_lowercase()))
        .collect()
}

impl Options {
    fn parse() -> Self {
        let mut input_path = None;
        let mut iterations = 7usize;
        let mut warmup_iterations = 2usize;
        let mut args = env::args().skip(1);

        while let Some(argument) = args.next() {
            match argument.as_str() {
                "--input" => {
                    input_path = Some(PathBuf::from(
                        args.next().expect("--input requires a value"),
                    ));
                }
                "--iterations" => {
                    iterations = parse_count(
                        &args.next().expect("--iterations requires a value"),
                        "--iterations",
                    );
                }
                "--warmup" => {
                    warmup_iterations =
                        parse_count(&args.next().expect("--warmup requires a value"), "--warmup");
                }
                _ => panic!("unknown argument '{argument}'"),
            }
        }

        assert!(
            iterations > 0,
            "at least one measured iteration is required"
        );
        Self {
            input_path: input_path.expect("--input is required"),
            iterations,
            warmup_iterations,
        }
    }
}

fn parse_count(value: &str, option: &str) -> usize {
    value
        .parse::<usize>()
        .unwrap_or_else(|_| panic!("{option} must be a non-negative integer"))
}
