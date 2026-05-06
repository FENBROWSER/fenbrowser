using System;
using System.Security.Cryptography;
using System.Text;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Errors;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class JsCryptoCompatibilityTests
    {
        [Fact]
        public void JsCrypto_SubtleDigest_ResolvesArrayBuffer()
        {
            var crypto = new JsCrypto();
            var subtle = Assert.IsType<FenObject>(crypto.Get("subtle").AsObject());
            var digest = subtle.Get("digest").AsFunction();

            var result = digest.Invoke(new[] { FenValue.FromString("SHA-256"), FenValue.FromString("abc") }, null);
            var thenable = Assert.IsType<FenObject>(result.AsObject());

            Assert.Equal("fulfilled", thenable.Get("__state").AsString());
            var buffer = Assert.IsType<JsArrayBuffer>(thenable.Get("__result").AsObject());
            Assert.Equal(32, buffer.Data.Length);
        }

        [Fact]
        public void JsCrypto_SubtleDigest_UnsupportedAlgorithm_Rejects()
        {
            var crypto = new JsCrypto();
            var subtle = Assert.IsType<FenObject>(crypto.Get("subtle").AsObject());
            var digest = subtle.Get("digest").AsFunction();

            var result = digest.Invoke(new[] { FenValue.FromString("MD5"), FenValue.FromString("abc") }, null);
            var thenable = Assert.IsType<FenObject>(result.AsObject());

            Assert.Equal("rejected", thenable.Get("__state").AsString());
            Assert.Contains("NotSupportedError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_GetRandomValues_FillsArrayLikeObject()
        {
            var crypto = new JsCrypto();
            var getRandomValues = crypto.Get("getRandomValues").AsFunction();

            var arrayLike = new FenObject();
            arrayLike.Set("length", FenValue.FromNumber(8));
            for (var i = 0; i < 8; i++)
            {
                arrayLike.Set(i.ToString(), FenValue.FromNumber(0));
            }

            var returned = getRandomValues.Invoke(new[] { FenValue.FromObject(arrayLike) }, null);
            Assert.True(returned.IsObject);
            Assert.Same(arrayLike, returned.AsObject());

            for (var i = 0; i < 8; i++)
            {
                Assert.True(arrayLike.Get(i.ToString()).IsNumber);
            }
        }

        [Fact]
        public void JsCrypto_GetRandomValues_EnforcesQuotaLimit()
        {
            var crypto = new JsCrypto();
            var getRandomValues = crypto.Get("getRandomValues").AsFunction();

            var oversized = new FenObject();
            oversized.Set("length", FenValue.FromNumber(70000));

            Assert.Throws<FenResourceError>(() =>
                getRandomValues.Invoke(new[] { FenValue.FromObject(oversized) }, null));
        }

        [Fact]
        public void JsCrypto_RandomUUID_ReturnsValidGuidString()
        {
            var crypto = new JsCrypto();
            var randomUuid = crypto.Get("randomUUID").AsFunction();

            var value = randomUuid.Invoke(Array.Empty<FenValue>(), null);
            Assert.True(Guid.TryParse(value.ToString(), out _));
        }

        [Fact]
        public void JsCrypto_SubtleHmac_ImportSignVerify_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();
            var verify = subtle.Get("verify").AsFunction();

            var keyBytes = Encoding.UTF8.GetBytes("fen-hmac-secret");
            var keyData = CreateArrayBuffer(keyBytes);
            var algorithm = CreateAlgorithm("HMAC", "SHA-256");
            var usages = CreateStringArray("sign", "verify");

            var importResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(keyData),
                    FenValue.FromObject(algorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(usages)
                },
                null);

            var importedThenable = AssertThenableState(importResult, "fulfilled");
            var key = Assert.IsType<FenObject>(importedThenable.Get("__result").AsObject());

            var data = FenValue.FromString("fenbrowser-payload");
            var signResult = sign.Invoke(
                new[]
                {
                    FenValue.FromString("HMAC"),
                    FenValue.FromObject(key),
                    data
                },
                null);

            var signThenable = AssertThenableState(signResult, "fulfilled");
            var signature = Assert.IsType<JsArrayBuffer>(signThenable.Get("__result").AsObject());
            Assert.True(signature.Data.Length > 0);

            var verifyResult = verify.Invoke(
                new[]
                {
                    FenValue.FromString("HMAC"),
                    FenValue.FromObject(key),
                    FenValue.FromObject(signature),
                    data
                },
                null);

            var verifyThenable = AssertThenableState(verifyResult, "fulfilled");
            Assert.True(verifyThenable.Get("__result").ToBoolean());
        }

        [Fact]
        public void JsCrypto_SubtleExportKey_NonExtractableRejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var exportKey = subtle.Get("exportKey").AsFunction();

            var importResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("locked-hmac-key"))),
                    FenValue.FromObject(CreateAlgorithm("HMAC", "SHA-256")),
                    FenValue.FromBoolean(false),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var importThenable = AssertThenableState(importResult, "fulfilled");
            var key = Assert.IsType<FenObject>(importThenable.Get("__result").AsObject());

            var exportResult = exportKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(key)
                },
                null);

            var exportThenable = AssertThenableState(exportResult, "rejected");
            Assert.Contains("InvalidAccessError", exportThenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleImportKey_UnsupportedUsageRejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();

            var result = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("bad-usage-key"))),
                    FenValue.FromObject(CreateAlgorithm("HMAC", "SHA-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("encrypt"))
                },
                null);

            var thenable = AssertThenableState(result, "rejected");
            Assert.Contains("InvalidAccessError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleSign_AlgorithmMismatchRejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();

            var importResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("mismatch-key"))),
                    FenValue.FromObject(CreateAlgorithm("HMAC", "SHA-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var importThenable = AssertThenableState(importResult, "fulfilled");
            var key = Assert.IsType<FenObject>(importThenable.Get("__result").AsObject());

            var signResult = sign.Invoke(
                new[]
                {
                    FenValue.FromString("RSASSA-PKCS1-v1_5"),
                    FenValue.FromObject(key),
                    FenValue.FromString("message")
                },
                null);

            var signThenable = AssertThenableState(signResult, "rejected");
            Assert.Contains("InvalidAccessError", signThenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleRsa_ImportSignVerify_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();
            var verify = subtle.Get("verify").AsFunction();

            using var rsa = RSA.Create(2048);
            var privateBytes = rsa.ExportPkcs8PrivateKey();
            var publicBytes = rsa.ExportSubjectPublicKeyInfo();
            var rsaAlgorithm = CreateAlgorithm("RSASSA-PKCS1-v1_5", "SHA-256");

            var privateKeyImport = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("pkcs8"),
                    FenValue.FromObject(CreateArrayBuffer(privateBytes)),
                    FenValue.FromObject(rsaAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign"))
                },
                null);

            var privateThenable = AssertThenableState(privateKeyImport, "fulfilled");
            var privateKey = Assert.IsType<FenObject>(privateThenable.Get("__result").AsObject());

            var publicKeyImport = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("spki"),
                    FenValue.FromObject(CreateArrayBuffer(publicBytes)),
                    FenValue.FromObject(CreateAlgorithm("RSASSA-PKCS1-v1_5", "SHA-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("verify"))
                },
                null);

            var publicThenable = AssertThenableState(publicKeyImport, "fulfilled");
            var publicKey = Assert.IsType<FenObject>(publicThenable.Get("__result").AsObject());

            var data = FenValue.FromString("fen-rsa-signature-data");
            var signResult = sign.Invoke(
                new[]
                {
                    FenValue.FromString("RSASSA-PKCS1-v1_5"),
                    FenValue.FromObject(privateKey),
                    data
                },
                null);

            var signThenable = AssertThenableState(signResult, "fulfilled");
            var signature = Assert.IsType<JsArrayBuffer>(signThenable.Get("__result").AsObject());

            var verifyResult = verify.Invoke(
                new[]
                {
                    FenValue.FromString("RSASSA-PKCS1-v1_5"),
                    FenValue.FromObject(publicKey),
                    FenValue.FromObject(signature),
                    data
                },
                null);

            var verifyThenable = AssertThenableState(verifyResult, "fulfilled");
            Assert.True(verifyThenable.Get("__result").ToBoolean());
        }

        [Fact]
        public void JsCrypto_SubtleGenerateHmacKey_SignVerify_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();
            var verify = subtle.Get("verify").AsFunction();

            var algorithm = CreateAlgorithm("HMAC", "SHA-256");
            algorithm.Set("length", FenValue.FromNumber(256));

            var generateResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(algorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var keyThenable = AssertThenableState(generateResult, "fulfilled");
            var key = Assert.IsType<FenObject>(keyThenable.Get("__result").AsObject());

            var data = FenValue.FromString("generated-hmac-data");
            var signatureResult = sign.Invoke(
                new[]
                {
                    FenValue.FromString("HMAC"),
                    FenValue.FromObject(key),
                    data
                },
                null);

            var signatureThenable = AssertThenableState(signatureResult, "fulfilled");
            var signature = Assert.IsType<JsArrayBuffer>(signatureThenable.Get("__result").AsObject());

            var verifyResult = verify.Invoke(
                new[]
                {
                    FenValue.FromString("HMAC"),
                    FenValue.FromObject(key),
                    FenValue.FromObject(signature),
                    data
                },
                null);

            var verifyThenable = AssertThenableState(verifyResult, "fulfilled");
            Assert.True(verifyThenable.Get("__result").ToBoolean());
        }

        [Fact]
        public void JsCrypto_SubtleGenerateRsaKeyPair_SignVerify_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();
            var verify = subtle.Get("verify").AsFunction();

            var algorithm = CreateAlgorithm("RSASSA-PKCS1-v1_5", "SHA-256");
            algorithm.Set("modulusLength", FenValue.FromNumber(1024));
            algorithm.Set("publicExponent", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x01, 0x00, 0x01 })));

            var generateResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(algorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var keyPairThenable = AssertThenableState(generateResult, "fulfilled");
            var keyPair = Assert.IsType<FenObject>(keyPairThenable.Get("__result").AsObject());
            var privateKey = Assert.IsType<FenObject>(keyPair.Get("privateKey").AsObject());
            var publicKey = Assert.IsType<FenObject>(keyPair.Get("publicKey").AsObject());

            var data = FenValue.FromString("generated-rsa-data");
            var signResult = sign.Invoke(
                new[]
                {
                    FenValue.FromString("RSASSA-PKCS1-v1_5"),
                    FenValue.FromObject(privateKey),
                    data
                },
                null);

            var signThenable = AssertThenableState(signResult, "fulfilled");
            var signature = Assert.IsType<JsArrayBuffer>(signThenable.Get("__result").AsObject());

            var verifyResult = verify.Invoke(
                new[]
                {
                    FenValue.FromString("RSASSA-PKCS1-v1_5"),
                    FenValue.FromObject(publicKey),
                    FenValue.FromObject(signature),
                    data
                },
                null);

            var verifyThenable = AssertThenableState(verifyResult, "fulfilled");
            Assert.True(verifyThenable.Get("__result").ToBoolean());
        }

        [Fact]
        public void JsCrypto_SubtleGenerateRsaKeyPair_UnsupportedExponentRejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();

            var algorithm = CreateAlgorithm("RSASSA-PKCS1-v1_5", "SHA-256");
            algorithm.Set("modulusLength", FenValue.FromNumber(1024));
            algorithm.Set("publicExponent", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x03 })));

            var generateResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(algorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var thenable = AssertThenableState(generateResult, "rejected");
            Assert.Contains("NotSupportedError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleGenerateAesGcmKey_EncryptDecrypt_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var encrypt = subtle.Get("encrypt").AsFunction();
            var decrypt = subtle.Get("decrypt").AsFunction();

            var generateAlgorithm = CreateAlgorithm("AES-GCM");
            generateAlgorithm.Set("length", FenValue.FromNumber(128));

            var keyResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(generateAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("encrypt", "decrypt"))
                },
                null);

            var keyThenable = AssertThenableState(keyResult, "fulfilled");
            var key = Assert.IsType<FenObject>(keyThenable.Get("__result").AsObject());

            var operationAlgorithm = CreateAlgorithm("AES-GCM");
            operationAlgorithm.Set("iv", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x40, 0x41 })));
            var plaintext = FenValue.FromString("fen-aes-gcm-message");

            var encryptResult = encrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(key),
                    plaintext
                },
                null);

            var encryptThenable = AssertThenableState(encryptResult, "fulfilled");
            var encryptedPayload = Assert.IsType<JsArrayBuffer>(encryptThenable.Get("__result").AsObject());
            Assert.True(encryptedPayload.Data.Length > 16);

            var decryptResult = decrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(key),
                    FenValue.FromObject(encryptedPayload)
                },
                null);

            var decryptThenable = AssertThenableState(decryptResult, "fulfilled");
            var decryptedPayload = Assert.IsType<JsArrayBuffer>(decryptThenable.Get("__result").AsObject());
            Assert.Equal("fen-aes-gcm-message", Encoding.UTF8.GetString(decryptedPayload.Data));
        }

        [Fact]
        public void JsCrypto_SubtleAesGcmImportExportRaw_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var exportKey = subtle.Get("exportKey").AsFunction();

            var rawKey = new byte[] { 0x12, 0x7A, 0xEE, 0x41, 0x99, 0x13, 0x8D, 0x05, 0xC0, 0x11, 0x22, 0x33, 0x67, 0x44, 0x10, 0xAF };
            var algorithm = CreateAlgorithm("AES-GCM");
            algorithm.Set("length", FenValue.FromNumber(128));

            var importResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(rawKey)),
                    FenValue.FromObject(algorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("encrypt", "decrypt"))
                },
                null);

            var importThenable = AssertThenableState(importResult, "fulfilled");
            var key = Assert.IsType<FenObject>(importThenable.Get("__result").AsObject());

            var exportResult = exportKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(key)
                },
                null);

            var exportThenable = AssertThenableState(exportResult, "fulfilled");
            var exported = Assert.IsType<JsArrayBuffer>(exportThenable.Get("__result").AsObject());
            Assert.Equal(rawKey, exported.Data);
        }

        private static FenObject GetSubtle(JsCrypto crypto)
        {
            return Assert.IsType<FenObject>(crypto.Get("subtle").AsObject());
        }

        private static FenObject AssertThenableState(FenValue result, string state)
        {
            var thenable = Assert.IsType<FenObject>(result.AsObject());
            var actualState = thenable.Get("__state").AsString();
            if (!string.Equals(state, actualState, StringComparison.Ordinal))
            {
                var reason = thenable.Get("__reason").ToString();
                throw new Xunit.Sdk.XunitException($"Expected thenable state '{state}' but got '{actualState}'. Reason: {reason}");
            }

            return thenable;
        }

        private static FenObject CreateAlgorithm(string name, string hashName = null)
        {
            var algorithm = new FenObject();
            algorithm.Set("name", FenValue.FromString(name));
            if (!string.IsNullOrWhiteSpace(hashName))
            {
                var hash = new FenObject();
                hash.Set("name", FenValue.FromString(hashName));
                algorithm.Set("hash", FenValue.FromObject(hash));
            }

            return algorithm;
        }

        private static FenObject CreateStringArray(params string[] items)
        {
            var array = FenObject.CreateArray();
            for (var i = 0; i < items.Length; i++)
            {
                array.Set(i.ToString(), FenValue.FromString(items[i]));
            }

            array.Set("length", FenValue.FromNumber(items.Length));
            return array;
        }

        private static JsArrayBuffer CreateArrayBuffer(byte[] bytes)
        {
            var buffer = new JsArrayBuffer(bytes.Length);
            Array.Copy(bytes, buffer.Data, bytes.Length);
            return buffer;
        }
    }
}
