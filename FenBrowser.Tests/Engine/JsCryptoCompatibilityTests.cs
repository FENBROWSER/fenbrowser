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
        public void JsCrypto_GetRandomValues_FillsUint8Array()
        {
            var crypto = new JsCrypto();
            var getRandomValues = crypto.Get("getRandomValues").AsFunction();

            var typedArray = new JsUint8Array(FenValue.FromNumber(8));
            var returned = getRandomValues.Invoke(new[] { FenValue.FromObject(typedArray) }, null);
            Assert.True(returned.IsObject);
            Assert.Same(typedArray, returned.AsObject());
            Assert.Equal(8, typedArray.Length);
            Assert.Equal(8, typedArray.Buffer.Data.Length);
        }

        [Fact]
        public void JsCrypto_GetRandomValues_ArrayLikeObject_Rejects()
        {
            var crypto = new JsCrypto();
            var getRandomValues = crypto.Get("getRandomValues").AsFunction();

            var arrayLike = new FenObject();
            arrayLike.Set("length", FenValue.FromNumber(8));

            Assert.Throws<FenTypeError>(() =>
                getRandomValues.Invoke(new[] { FenValue.FromObject(arrayLike) }, null));
        }

        [Fact]
        public void JsCrypto_GetRandomValues_Float32Array_Rejects()
        {
            var crypto = new JsCrypto();
            var getRandomValues = crypto.Get("getRandomValues").AsFunction();
            var floatArray = new JsFloat32Array(FenValue.FromNumber(8));

            Assert.Throws<FenTypeError>(() =>
                getRandomValues.Invoke(new[] { FenValue.FromObject(floatArray) }, null));
        }

        [Fact]
        public void JsCrypto_GetRandomValues_EnforcesQuotaLimit()
        {
            var crypto = new JsCrypto();
            var getRandomValues = crypto.Get("getRandomValues").AsFunction();

            var oversized = new JsUint8Array(FenValue.FromNumber(70000));

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
        public void JsCrypto_SubtleImportKey_ArrayLikeNonIntegerLength_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();

            var keyData = new FenObject();
            keyData.Set("length", FenValue.FromNumber(3.5));
            keyData.Set("0", FenValue.FromNumber(1));
            keyData.Set("1", FenValue.FromNumber(2));
            keyData.Set("2", FenValue.FromNumber(3));

            var result = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(keyData),
                    FenValue.FromObject(CreateAlgorithm("HMAC", "SHA-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var thenable = AssertThenableState(result, "rejected");
            Assert.Contains("TypeError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleImportKey_ArrayLikeNonNumericElement_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();

            var keyData = new FenObject();
            keyData.Set("length", FenValue.FromNumber(3));
            keyData.Set("0", FenValue.FromNumber(1));
            keyData.Set("1", FenValue.FromString("bad"));
            keyData.Set("2", FenValue.FromNumber(3));

            var result = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(keyData),
                    FenValue.FromObject(CreateAlgorithm("HMAC", "SHA-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var thenable = AssertThenableState(result, "rejected");
            Assert.Contains("TypeError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
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
        public void JsCrypto_SubtleRsa_OperationHashMismatch_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();

            using var rsa = RSA.Create(2048);
            var privateBytes = rsa.ExportPkcs8PrivateKey();
            var privateKeyImport = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("pkcs8"),
                    FenValue.FromObject(CreateArrayBuffer(privateBytes)),
                    FenValue.FromObject(CreateAlgorithm("RSASSA-PKCS1-v1_5", "SHA-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign"))
                },
                null);

            var privateThenable = AssertThenableState(privateKeyImport, "fulfilled");
            var privateKey = Assert.IsType<FenObject>(privateThenable.Get("__result").AsObject());

            var signResult = sign.Invoke(
                new[]
                {
                    FenValue.FromObject(CreateAlgorithm("RSASSA-PKCS1-v1_5", "SHA-384")),
                    FenValue.FromObject(privateKey),
                    FenValue.FromString("rsa-hash-mismatch")
                },
                null);

            var thenable = AssertThenableState(signResult, "rejected");
            Assert.Contains("InvalidAccessError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
            Assert.Contains("hash", thenable.Get("__reason").ToString(), StringComparison.OrdinalIgnoreCase);
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
        public void JsCrypto_SubtleGenerateRsaOaepKey_EncryptDecrypt_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var encrypt = subtle.Get("encrypt").AsFunction();
            var decrypt = subtle.Get("decrypt").AsFunction();

            var algorithm = CreateAlgorithm("RSA-OAEP", "SHA-256");
            algorithm.Set("modulusLength", FenValue.FromNumber(1024));
            algorithm.Set("publicExponent", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x01, 0x00, 0x01 })));

            var generateResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(algorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("encrypt", "decrypt"))
                },
                null);

            var keyPairThenable = AssertThenableState(generateResult, "fulfilled");
            var keyPair = Assert.IsType<FenObject>(keyPairThenable.Get("__result").AsObject());
            var privateKey = Assert.IsType<FenObject>(keyPair.Get("privateKey").AsObject());
            var publicKey = Assert.IsType<FenObject>(keyPair.Get("publicKey").AsObject());

            var operationAlgorithm = CreateAlgorithm("RSA-OAEP");
            var plaintext = FenValue.FromString("rsa-oaep-encrypt-decrypt-message");
            var encryptResult = encrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(publicKey),
                    plaintext
                },
                null);

            var encryptThenable = AssertThenableState(encryptResult, "fulfilled");
            var encryptedPayload = Assert.IsType<JsArrayBuffer>(encryptThenable.Get("__result").AsObject());
            Assert.True(encryptedPayload.Data.Length > 0);

            var decryptResult = decrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(privateKey),
                    FenValue.FromObject(encryptedPayload)
                },
                null);

            var decryptThenable = AssertThenableState(decryptResult, "fulfilled");
            var decryptedPayload = Assert.IsType<JsArrayBuffer>(decryptThenable.Get("__result").AsObject());
            Assert.Equal("rsa-oaep-encrypt-decrypt-message", Encoding.UTF8.GetString(decryptedPayload.Data));
        }

        [Fact]
        public void JsCrypto_SubtleRsaOaep_WrapUnwrapKey_HmacRoundTrip_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var importKey = subtle.Get("importKey").AsFunction();
            var wrapKey = subtle.Get("wrapKey").AsFunction();
            var unwrapKey = subtle.Get("unwrapKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();
            var verify = subtle.Get("verify").AsFunction();

            var keyToWrapImport = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("rsa-oaep-wrap-target-hmac"))),
                    FenValue.FromObject(CreateAlgorithm("HMAC", "SHA-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var keyToWrapThenable = AssertThenableState(keyToWrapImport, "fulfilled");
            var keyToWrap = Assert.IsType<FenObject>(keyToWrapThenable.Get("__result").AsObject());

            var rsaAlgorithm = CreateAlgorithm("RSA-OAEP", "SHA-256");
            rsaAlgorithm.Set("modulusLength", FenValue.FromNumber(1024));
            rsaAlgorithm.Set("publicExponent", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x01, 0x00, 0x01 })));
            var keyPairResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(rsaAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("wrapKey", "unwrapKey"))
                },
                null);

            var keyPairThenable = AssertThenableState(keyPairResult, "fulfilled");
            var keyPair = Assert.IsType<FenObject>(keyPairThenable.Get("__result").AsObject());
            var privateKey = Assert.IsType<FenObject>(keyPair.Get("privateKey").AsObject());
            var publicKey = Assert.IsType<FenObject>(keyPair.Get("publicKey").AsObject());

            var wrapResult = wrapKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(keyToWrap),
                    FenValue.FromObject(publicKey),
                    FenValue.FromObject(CreateAlgorithm("RSA-OAEP"))
                },
                null);

            var wrapThenable = AssertThenableState(wrapResult, "fulfilled");
            var wrappedPayload = Assert.IsType<JsArrayBuffer>(wrapThenable.Get("__result").AsObject());
            Assert.True(wrappedPayload.Data.Length > 0);

            var unwrapResult = unwrapKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(wrappedPayload),
                    FenValue.FromObject(privateKey),
                    FenValue.FromObject(CreateAlgorithm("RSA-OAEP")),
                    FenValue.FromObject(CreateAlgorithm("HMAC", "SHA-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var unwrapThenable = AssertThenableState(unwrapResult, "fulfilled");
            var unwrappedKey = Assert.IsType<FenObject>(unwrapThenable.Get("__result").AsObject());

            var message = FenValue.FromString("rsa-oaep-wrapped-key-sign-verify");
            var signatureResult = sign.Invoke(
                new[]
                {
                    FenValue.FromString("HMAC"),
                    FenValue.FromObject(unwrappedKey),
                    message
                },
                null);

            var signatureThenable = AssertThenableState(signatureResult, "fulfilled");
            var signature = Assert.IsType<JsArrayBuffer>(signatureThenable.Get("__result").AsObject());

            var verifyResult = verify.Invoke(
                new[]
                {
                    FenValue.FromString("HMAC"),
                    FenValue.FromObject(unwrappedKey),
                    FenValue.FromObject(signature),
                    message
                },
                null);

            var verifyThenable = AssertThenableState(verifyResult, "fulfilled");
            Assert.True(verifyThenable.Get("__result").ToBoolean());
        }

        [Fact]
        public void JsCrypto_SubtleRsaOaep_EncryptWithLabel_RejectsUntilSupported()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var encrypt = subtle.Get("encrypt").AsFunction();

            var algorithm = CreateAlgorithm("RSA-OAEP", "SHA-256");
            algorithm.Set("modulusLength", FenValue.FromNumber(1024));
            algorithm.Set("publicExponent", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x01, 0x00, 0x01 })));
            var keyPairResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(algorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("encrypt", "decrypt"))
                },
                null);

            var keyPairThenable = AssertThenableState(keyPairResult, "fulfilled");
            var keyPair = Assert.IsType<FenObject>(keyPairThenable.Get("__result").AsObject());
            var publicKey = Assert.IsType<FenObject>(keyPair.Get("publicKey").AsObject());

            var operationAlgorithm = CreateAlgorithm("RSA-OAEP");
            operationAlgorithm.Set("label", FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("oaep-label"))));

            var encryptResult = encrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(publicKey),
                    FenValue.FromString("payload")
                },
                null);

            var thenable = AssertThenableState(encryptResult, "rejected");
            Assert.Contains("NotSupportedError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleRsaOaep_OperationHashMismatch_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var encrypt = subtle.Get("encrypt").AsFunction();

            var algorithm = CreateAlgorithm("RSA-OAEP", "SHA-256");
            algorithm.Set("modulusLength", FenValue.FromNumber(1024));
            algorithm.Set("publicExponent", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x01, 0x00, 0x01 })));
            var keyPairResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(algorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("encrypt", "decrypt"))
                },
                null);

            var keyPairThenable = AssertThenableState(keyPairResult, "fulfilled");
            var keyPair = Assert.IsType<FenObject>(keyPairThenable.Get("__result").AsObject());
            var publicKey = Assert.IsType<FenObject>(keyPair.Get("publicKey").AsObject());

            var operationAlgorithm = CreateAlgorithm("RSA-OAEP", "SHA-384");
            var encryptResult = encrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(publicKey),
                    FenValue.FromString("hash-mismatch")
                },
                null);

            var thenable = AssertThenableState(encryptResult, "rejected");
            Assert.Contains("InvalidAccessError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
            Assert.Contains("hash", thenable.Get("__reason").ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void JsCrypto_SubtleGenerateRsaPssKey_SignVerify_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();
            var verify = subtle.Get("verify").AsFunction();

            var algorithm = CreateAlgorithm("RSA-PSS", "SHA-256");
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

            var signAlgorithm = CreateAlgorithm("RSA-PSS");
            signAlgorithm.Set("saltLength", FenValue.FromNumber(32));
            var data = FenValue.FromString("rsa-pss-signature-data");
            var signResult = sign.Invoke(
                new[]
                {
                    FenValue.FromObject(signAlgorithm),
                    FenValue.FromObject(privateKey),
                    data
                },
                null);

            var signThenable = AssertThenableState(signResult, "fulfilled");
            var signature = Assert.IsType<JsArrayBuffer>(signThenable.Get("__result").AsObject());

            var verifyResult = verify.Invoke(
                new[]
                {
                    FenValue.FromObject(signAlgorithm),
                    FenValue.FromObject(publicKey),
                    FenValue.FromObject(signature),
                    data
                },
                null);

            var verifyThenable = AssertThenableState(verifyResult, "fulfilled");
            Assert.True(verifyThenable.Get("__result").ToBoolean());
        }

        [Fact]
        public void JsCrypto_SubtleRsaPss_ImportSignVerify_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();
            var verify = subtle.Get("verify").AsFunction();

            using var rsa = RSA.Create(2048);
            var privateBytes = rsa.ExportPkcs8PrivateKey();
            var publicBytes = rsa.ExportSubjectPublicKeyInfo();
            var algorithm = CreateAlgorithm("RSA-PSS", "SHA-384");

            var privateKeyResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("pkcs8"),
                    FenValue.FromObject(CreateArrayBuffer(privateBytes)),
                    FenValue.FromObject(algorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign"))
                },
                null);

            var privateThenable = AssertThenableState(privateKeyResult, "fulfilled");
            var privateKey = Assert.IsType<FenObject>(privateThenable.Get("__result").AsObject());

            var publicKeyResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("spki"),
                    FenValue.FromObject(CreateArrayBuffer(publicBytes)),
                    FenValue.FromObject(CreateAlgorithm("RSA-PSS", "SHA-384")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("verify"))
                },
                null);

            var publicThenable = AssertThenableState(publicKeyResult, "fulfilled");
            var publicKey = Assert.IsType<FenObject>(publicThenable.Get("__result").AsObject());

            var signAlgorithm = CreateAlgorithm("RSA-PSS");
            signAlgorithm.Set("saltLength", FenValue.FromNumber(48));
            var data = FenValue.FromString("rsa-pss-import-signature-data");
            var signResult = sign.Invoke(
                new[]
                {
                    FenValue.FromObject(signAlgorithm),
                    FenValue.FromObject(privateKey),
                    data
                },
                null);

            var signThenable = AssertThenableState(signResult, "fulfilled");
            var signature = Assert.IsType<JsArrayBuffer>(signThenable.Get("__result").AsObject());

            var verifyResult = verify.Invoke(
                new[]
                {
                    FenValue.FromObject(signAlgorithm),
                    FenValue.FromObject(publicKey),
                    FenValue.FromObject(signature),
                    data
                },
                null);

            var verifyThenable = AssertThenableState(verifyResult, "fulfilled");
            Assert.True(verifyThenable.Get("__result").ToBoolean());
        }

        [Fact]
        public void JsCrypto_SubtleRsaPss_OperationHashMismatch_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();

            using var rsa = RSA.Create(2048);
            var privateBytes = rsa.ExportPkcs8PrivateKey();
            var privateKeyResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("pkcs8"),
                    FenValue.FromObject(CreateArrayBuffer(privateBytes)),
                    FenValue.FromObject(CreateAlgorithm("RSA-PSS", "SHA-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign"))
                },
                null);

            var privateThenable = AssertThenableState(privateKeyResult, "fulfilled");
            var privateKey = Assert.IsType<FenObject>(privateThenable.Get("__result").AsObject());

            var signAlgorithm = CreateAlgorithm("RSA-PSS", "SHA-384");
            signAlgorithm.Set("saltLength", FenValue.FromNumber(48));
            var signResult = sign.Invoke(
                new[]
                {
                    FenValue.FromObject(signAlgorithm),
                    FenValue.FromObject(privateKey),
                    FenValue.FromString("rsa-pss-hash-mismatch")
                },
                null);

            var thenable = AssertThenableState(signResult, "rejected");
            Assert.Contains("InvalidAccessError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
            Assert.Contains("hash", thenable.Get("__reason").ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void JsCrypto_SubtleRsaPss_SignWithUnsupportedSaltLength_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();

            var algorithm = CreateAlgorithm("RSA-PSS", "SHA-256");
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

            var signAlgorithm = CreateAlgorithm("RSA-PSS");
            signAlgorithm.Set("saltLength", FenValue.FromNumber(16));
            var signResult = sign.Invoke(
                new[]
                {
                    FenValue.FromObject(signAlgorithm),
                    FenValue.FromObject(privateKey),
                    FenValue.FromString("rsa-pss-bad-salt-length")
                },
                null);

            var thenable = AssertThenableState(signResult, "rejected");
            Assert.Contains("NotSupportedError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleGenerateEcdsaKey_SignVerify_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();
            var verify = subtle.Get("verify").AsFunction();

            var algorithm = CreateAlgorithm("ECDSA");
            algorithm.Set("namedCurve", FenValue.FromString("P-256"));

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

            var signAlgorithm = CreateAlgorithm("ECDSA", "SHA-256");
            var data = FenValue.FromString("ecdsa-generated-signature-data");
            var signResult = sign.Invoke(
                new[]
                {
                    FenValue.FromObject(signAlgorithm),
                    FenValue.FromObject(privateKey),
                    data
                },
                null);

            var signThenable = AssertThenableState(signResult, "fulfilled");
            var signature = Assert.IsType<JsArrayBuffer>(signThenable.Get("__result").AsObject());

            var verifyResult = verify.Invoke(
                new[]
                {
                    FenValue.FromObject(signAlgorithm),
                    FenValue.FromObject(publicKey),
                    FenValue.FromObject(signature),
                    data
                },
                null);

            var verifyThenable = AssertThenableState(verifyResult, "fulfilled");
            Assert.True(verifyThenable.Get("__result").ToBoolean());
        }

        [Fact]
        public void JsCrypto_SubtleEcdsa_ImportSignVerify_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();
            var verify = subtle.Get("verify").AsFunction();

            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            var privateBytes = ecdsa.ExportPkcs8PrivateKey();
            var publicBytes = ecdsa.ExportSubjectPublicKeyInfo();

            var privateAlgorithm = CreateAlgorithm("ECDSA");
            privateAlgorithm.Set("namedCurve", FenValue.FromString("P-384"));
            var privateImportResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("pkcs8"),
                    FenValue.FromObject(CreateArrayBuffer(privateBytes)),
                    FenValue.FromObject(privateAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign"))
                },
                null);

            var privateThenable = AssertThenableState(privateImportResult, "fulfilled");
            var privateKey = Assert.IsType<FenObject>(privateThenable.Get("__result").AsObject());

            var publicAlgorithm = CreateAlgorithm("ECDSA");
            publicAlgorithm.Set("namedCurve", FenValue.FromString("P-384"));
            var publicImportResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("spki"),
                    FenValue.FromObject(CreateArrayBuffer(publicBytes)),
                    FenValue.FromObject(publicAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("verify"))
                },
                null);

            var publicThenable = AssertThenableState(publicImportResult, "fulfilled");
            var publicKey = Assert.IsType<FenObject>(publicThenable.Get("__result").AsObject());

            var signAlgorithm = CreateAlgorithm("ECDSA", "SHA-384");
            var data = FenValue.FromString("ecdsa-import-signature-data");
            var signResult = sign.Invoke(
                new[]
                {
                    FenValue.FromObject(signAlgorithm),
                    FenValue.FromObject(privateKey),
                    data
                },
                null);

            var signThenable = AssertThenableState(signResult, "fulfilled");
            var signature = Assert.IsType<JsArrayBuffer>(signThenable.Get("__result").AsObject());

            var verifyResult = verify.Invoke(
                new[]
                {
                    FenValue.FromObject(signAlgorithm),
                    FenValue.FromObject(publicKey),
                    FenValue.FromObject(signature),
                    data
                },
                null);

            var verifyThenable = AssertThenableState(verifyResult, "fulfilled");
            Assert.True(verifyThenable.Get("__result").ToBoolean());
        }

        [Fact]
        public void JsCrypto_SubtleImportEcdsaKey_CurveMismatch_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();

            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            var privateBytes = ecdsa.ExportPkcs8PrivateKey();

            var algorithm = CreateAlgorithm("ECDSA");
            algorithm.Set("namedCurve", FenValue.FromString("P-256"));

            var importResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("pkcs8"),
                    FenValue.FromObject(CreateArrayBuffer(privateBytes)),
                    FenValue.FromObject(algorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign"))
                },
                null);

            var thenable = AssertThenableState(importResult, "rejected");
            Assert.Contains("DataError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleEcdsa_SignWithoutHash_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();

            var algorithm = CreateAlgorithm("ECDSA");
            algorithm.Set("namedCurve", FenValue.FromString("P-256"));
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

            var signResult = sign.Invoke(
                new[]
                {
                    FenValue.FromObject(CreateAlgorithm("ECDSA")),
                    FenValue.FromObject(privateKey),
                    FenValue.FromString("ecdsa-missing-hash")
                },
                null);

            var thenable = AssertThenableState(signResult, "rejected");
            Assert.Contains("NotSupportedError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleGenerateAesCbcKey_EncryptDecrypt_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var encrypt = subtle.Get("encrypt").AsFunction();
            var decrypt = subtle.Get("decrypt").AsFunction();

            var generateAlgorithm = CreateAlgorithm("AES-CBC");
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

            var operationAlgorithm = CreateAlgorithm("AES-CBC");
            operationAlgorithm.Set("iv", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE })));
            var plaintext = FenValue.FromString("fen-aes-cbc-message");

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
            Assert.True(encryptedPayload.Data.Length > 0);

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
            Assert.Equal("fen-aes-cbc-message", Encoding.UTF8.GetString(decryptedPayload.Data));
        }

        [Fact]
        public void JsCrypto_SubtleAesCbcImportExportRaw_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var exportKey = subtle.Get("exportKey").AsFunction();

            var rawKey = new byte[] { 0x11, 0x72, 0xE1, 0x4A, 0x90, 0x22, 0x8D, 0x07, 0xD0, 0x12, 0x21, 0x34, 0x68, 0x45, 0x13, 0xAE };
            var algorithm = CreateAlgorithm("AES-CBC");
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

        [Fact]
        public void JsCrypto_SubtleEncrypt_AesCbcInvalidIv_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var encrypt = subtle.Get("encrypt").AsFunction();

            var generateAlgorithm = CreateAlgorithm("AES-CBC");
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

            var operationAlgorithm = CreateAlgorithm("AES-CBC");
            operationAlgorithm.Set("iv", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x10, 0x20, 0x30, 0x40 })));

            var encryptResult = encrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(key),
                    FenValue.FromString("invalid-iv-test")
                },
                null);

            var thenable = AssertThenableState(encryptResult, "rejected");
            Assert.Contains("TypeError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleGenerateAesCtrKey_EncryptDecrypt_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var encrypt = subtle.Get("encrypt").AsFunction();
            var decrypt = subtle.Get("decrypt").AsFunction();

            var generateAlgorithm = CreateAlgorithm("AES-CTR");
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

            var operationAlgorithm = CreateAlgorithm("AES-CTR");
            operationAlgorithm.Set("counter", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x10, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87, 0x98, 0xA9, 0xBA, 0xCB, 0xDC, 0xED, 0xFE, 0x0F })));
            operationAlgorithm.Set("length", FenValue.FromNumber(128));
            var plaintext = FenValue.FromString("fen-aes-ctr-message");

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
            Assert.True(encryptedPayload.Data.Length > 0);

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
            Assert.Equal("fen-aes-ctr-message", Encoding.UTF8.GetString(decryptedPayload.Data));
        }

        [Fact]
        public void JsCrypto_SubtleAesCtrImportExportRaw_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var exportKey = subtle.Get("exportKey").AsFunction();

            var rawKey = new byte[] { 0x21, 0x62, 0xE2, 0x5A, 0x80, 0x12, 0x8D, 0x0A, 0xC1, 0x10, 0x24, 0x37, 0x69, 0x48, 0x15, 0xAD };
            var algorithm = CreateAlgorithm("AES-CTR");
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

        [Fact]
        public void JsCrypto_SubtleEncryptDecrypt_AesCtrLength64_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var encrypt = subtle.Get("encrypt").AsFunction();
            var decrypt = subtle.Get("decrypt").AsFunction();

            var generateAlgorithm = CreateAlgorithm("AES-CTR");
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

            var operationAlgorithm = CreateAlgorithm("AES-CTR");
            operationAlgorithm.Set("counter", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x10, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87, 0x98, 0xA9, 0xBA, 0xCB, 0xDC, 0xED, 0xFE, 0x0F })));
            operationAlgorithm.Set("length", FenValue.FromNumber(64));

            var plaintext = FenValue.FromString("supported-ctr-length");

            var encryptResult = encrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(key),
                    plaintext
                },
                null);

            var encryptThenable = AssertThenableState(encryptResult, "fulfilled");
            var ciphertext = Assert.IsType<JsArrayBuffer>(encryptThenable.Get("__result").AsObject());

            var decryptResult = decrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(key),
                    FenValue.FromObject(ciphertext)
                },
                null);

            var decryptThenable = AssertThenableState(decryptResult, "fulfilled");
            var decrypted = Assert.IsType<JsArrayBuffer>(decryptThenable.Get("__result").AsObject());
            Assert.Equal("supported-ctr-length", Encoding.UTF8.GetString(decrypted.Data));
        }

        [Fact]
        public void JsCrypto_SubtleEncrypt_AesCtrCounterOverflow_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var encrypt = subtle.Get("encrypt").AsFunction();

            var generateAlgorithm = CreateAlgorithm("AES-CTR");
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

            var operationAlgorithm = CreateAlgorithm("AES-CTR");
            operationAlgorithm.Set("counter", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF })));
            operationAlgorithm.Set("length", FenValue.FromNumber(8));

            var encryptResult = encrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(key),
                    FenValue.FromString("ctr-overflow-two-blocks")
                },
                null);

            var thenable = AssertThenableState(encryptResult, "rejected");
            Assert.Contains("OperationError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
            Assert.Contains("overflow", thenable.Get("__reason").ToString(), StringComparison.OrdinalIgnoreCase);
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

        [Fact]
        public void JsCrypto_SubtleWrapUnwrapKey_HmacRawRoundTrip_Resolves()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var generateKey = subtle.Get("generateKey").AsFunction();
            var wrapKey = subtle.Get("wrapKey").AsFunction();
            var unwrapKey = subtle.Get("unwrapKey").AsFunction();
            var sign = subtle.Get("sign").AsFunction();
            var verify = subtle.Get("verify").AsFunction();

            var keyToWrapImport = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("wrap-target-hmac-key"))),
                    FenValue.FromObject(CreateAlgorithm("HMAC", "SHA-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var keyToWrapThenable = AssertThenableState(keyToWrapImport, "fulfilled");
            var keyToWrap = Assert.IsType<FenObject>(keyToWrapThenable.Get("__result").AsObject());

            var wrappingAlgorithm = CreateAlgorithm("AES-GCM");
            wrappingAlgorithm.Set("length", FenValue.FromNumber(128));
            var wrappingKeyResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(wrappingAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("wrapKey", "unwrapKey"))
                },
                null);

            var wrappingKeyThenable = AssertThenableState(wrappingKeyResult, "fulfilled");
            var wrappingKey = Assert.IsType<FenObject>(wrappingKeyThenable.Get("__result").AsObject());

            var wrapParams = CreateAlgorithm("AES-GCM");
            wrapParams.Set("iv", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x41, 0x42, 0x43 })));

            var wrapResult = wrapKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(keyToWrap),
                    FenValue.FromObject(wrappingKey),
                    FenValue.FromObject(wrapParams)
                },
                null);

            var wrapThenable = AssertThenableState(wrapResult, "fulfilled");
            var wrappedPayload = Assert.IsType<JsArrayBuffer>(wrapThenable.Get("__result").AsObject());
            Assert.True(wrappedPayload.Data.Length > 0);

            var unwrapResult = unwrapKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(wrappedPayload),
                    FenValue.FromObject(wrappingKey),
                    FenValue.FromObject(wrapParams),
                    FenValue.FromObject(CreateAlgorithm("HMAC", "SHA-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var unwrapThenable = AssertThenableState(unwrapResult, "fulfilled");
            var unwrappedKey = Assert.IsType<FenObject>(unwrapThenable.Get("__result").AsObject());

            var message = FenValue.FromString("wrapped-key-sign-verify");
            var signatureResult = sign.Invoke(
                new[]
                {
                    FenValue.FromString("HMAC"),
                    FenValue.FromObject(unwrappedKey),
                    message
                },
                null);

            var signatureThenable = AssertThenableState(signatureResult, "fulfilled");
            var signature = Assert.IsType<JsArrayBuffer>(signatureThenable.Get("__result").AsObject());

            var verifyResult = verify.Invoke(
                new[]
                {
                    FenValue.FromString("HMAC"),
                    FenValue.FromObject(unwrappedKey),
                    FenValue.FromObject(signature),
                    message
                },
                null);

            var verifyThenable = AssertThenableState(verifyResult, "fulfilled");
            Assert.True(verifyThenable.Get("__result").ToBoolean());
        }

        [Fact]
        public void JsCrypto_SubtleWrapKey_NonExtractableTarget_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var generateKey = subtle.Get("generateKey").AsFunction();
            var wrapKey = subtle.Get("wrapKey").AsFunction();

            var nonExtractableKeyResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("non-extractable-wrap-target"))),
                    FenValue.FromObject(CreateAlgorithm("HMAC", "SHA-256")),
                    FenValue.FromBoolean(false),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var nonExtractableKeyThenable = AssertThenableState(nonExtractableKeyResult, "fulfilled");
            var nonExtractableKey = Assert.IsType<FenObject>(nonExtractableKeyThenable.Get("__result").AsObject());

            var wrappingAlgorithm = CreateAlgorithm("AES-GCM");
            wrappingAlgorithm.Set("length", FenValue.FromNumber(128));
            var wrappingKeyResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(wrappingAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("wrapKey", "unwrapKey"))
                },
                null);

            var wrappingKeyThenable = AssertThenableState(wrappingKeyResult, "fulfilled");
            var wrappingKey = Assert.IsType<FenObject>(wrappingKeyThenable.Get("__result").AsObject());

            var wrapParams = CreateAlgorithm("AES-GCM");
            wrapParams.Set("iv", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x61, 0x62, 0x63 })));

            var wrapResult = wrapKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(nonExtractableKey),
                    FenValue.FromObject(wrappingKey),
                    FenValue.FromObject(wrapParams)
                },
                null);

            var thenable = AssertThenableState(wrapResult, "rejected");
            Assert.Contains("InvalidAccessError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleUnwrapKey_WithoutUnwrapUsage_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var generateKey = subtle.Get("generateKey").AsFunction();
            var wrapKey = subtle.Get("wrapKey").AsFunction();
            var unwrapKey = subtle.Get("unwrapKey").AsFunction();

            var keyToWrapImport = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("unwrap-usage-target-key"))),
                    FenValue.FromObject(CreateAlgorithm("HMAC", "SHA-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var keyToWrapThenable = AssertThenableState(keyToWrapImport, "fulfilled");
            var keyToWrap = Assert.IsType<FenObject>(keyToWrapThenable.Get("__result").AsObject());

            var wrappingAlgorithm = CreateAlgorithm("AES-GCM");
            wrappingAlgorithm.Set("length", FenValue.FromNumber(128));
            var wrappingKeyResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(wrappingAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("wrapKey"))
                },
                null);

            var wrappingKeyThenable = AssertThenableState(wrappingKeyResult, "fulfilled");
            var wrappingKey = Assert.IsType<FenObject>(wrappingKeyThenable.Get("__result").AsObject());

            var wrapParams = CreateAlgorithm("AES-GCM");
            wrapParams.Set("iv", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x81, 0x82, 0x83 })));

            var wrapResult = wrapKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(keyToWrap),
                    FenValue.FromObject(wrappingKey),
                    FenValue.FromObject(wrapParams)
                },
                null);

            var wrapThenable = AssertThenableState(wrapResult, "fulfilled");
            var wrappedPayload = Assert.IsType<JsArrayBuffer>(wrapThenable.Get("__result").AsObject());

            var unwrapResult = unwrapKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(wrappedPayload),
                    FenValue.FromObject(wrappingKey),
                    FenValue.FromObject(wrapParams),
                    FenValue.FromObject(CreateAlgorithm("HMAC", "SHA-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("sign", "verify"))
                },
                null);

            var unwrapThenable = AssertThenableState(unwrapResult, "rejected");
            Assert.Contains("InvalidAccessError", unwrapThenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleDeriveBits_Ecdh_ResolvesAndMatchesAcrossPeers()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var deriveBits = subtle.Get("deriveBits").AsFunction();

            var ecdhAlgorithm = CreateAlgorithm("ECDH");
            ecdhAlgorithm.Set("namedCurve", FenValue.FromString("P-256"));

            var aliceKeyPairResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(ecdhAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("deriveBits"))
                },
                null);
            var aliceThenable = AssertThenableState(aliceKeyPairResult, "fulfilled");
            var aliceKeyPair = Assert.IsType<FenObject>(aliceThenable.Get("__result").AsObject());
            var alicePrivateKey = Assert.IsType<FenObject>(aliceKeyPair.Get("privateKey").AsObject());
            var alicePublicKey = Assert.IsType<FenObject>(aliceKeyPair.Get("publicKey").AsObject());

            var bobKeyPairResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(CreateAlgorithmWithNamedCurve("ECDH", "P-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("deriveBits"))
                },
                null);
            var bobThenable = AssertThenableState(bobKeyPairResult, "fulfilled");
            var bobKeyPair = Assert.IsType<FenObject>(bobThenable.Get("__result").AsObject());
            var bobPrivateKey = Assert.IsType<FenObject>(bobKeyPair.Get("privateKey").AsObject());
            var bobPublicKey = Assert.IsType<FenObject>(bobKeyPair.Get("publicKey").AsObject());

            var aliceDeriveAlgorithm = CreateAlgorithm("ECDH");
            aliceDeriveAlgorithm.Set("public", FenValue.FromObject(bobPublicKey));
            var aliceBitsResult = deriveBits.Invoke(
                new[]
                {
                    FenValue.FromObject(aliceDeriveAlgorithm),
                    FenValue.FromObject(alicePrivateKey),
                    FenValue.FromNumber(128)
                },
                null);

            var aliceBitsThenable = AssertThenableState(aliceBitsResult, "fulfilled");
            var aliceBits = Assert.IsType<JsArrayBuffer>(aliceBitsThenable.Get("__result").AsObject());

            var bobDeriveAlgorithm = CreateAlgorithm("ECDH");
            bobDeriveAlgorithm.Set("public", FenValue.FromObject(alicePublicKey));
            var bobBitsResult = deriveBits.Invoke(
                new[]
                {
                    FenValue.FromObject(bobDeriveAlgorithm),
                    FenValue.FromObject(bobPrivateKey),
                    FenValue.FromNumber(128)
                },
                null);

            var bobBitsThenable = AssertThenableState(bobBitsResult, "fulfilled");
            var bobBits = Assert.IsType<JsArrayBuffer>(bobBitsThenable.Get("__result").AsObject());

            Assert.Equal(16, aliceBits.Data.Length);
            Assert.Equal(aliceBits.Data, bobBits.Data);
        }

        [Fact]
        public void JsCrypto_SubtleDeriveKey_Ecdh_ToAesGcm_EncryptDecrypt_RoundTripsAcrossPeers()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var deriveKey = subtle.Get("deriveKey").AsFunction();
            var encrypt = subtle.Get("encrypt").AsFunction();
            var decrypt = subtle.Get("decrypt").AsFunction();

            var ecdhAlgorithm = CreateAlgorithm("ECDH");
            ecdhAlgorithm.Set("namedCurve", FenValue.FromString("P-256"));

            var aliceKeyPairResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(ecdhAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("deriveKey"))
                },
                null);
            var aliceThenable = AssertThenableState(aliceKeyPairResult, "fulfilled");
            var aliceKeyPair = Assert.IsType<FenObject>(aliceThenable.Get("__result").AsObject());
            var alicePrivateKey = Assert.IsType<FenObject>(aliceKeyPair.Get("privateKey").AsObject());
            var alicePublicKey = Assert.IsType<FenObject>(aliceKeyPair.Get("publicKey").AsObject());

            var bobKeyPairResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(CreateAlgorithmWithNamedCurve("ECDH", "P-256")),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("deriveKey"))
                },
                null);
            var bobThenable = AssertThenableState(bobKeyPairResult, "fulfilled");
            var bobKeyPair = Assert.IsType<FenObject>(bobThenable.Get("__result").AsObject());
            var bobPrivateKey = Assert.IsType<FenObject>(bobKeyPair.Get("privateKey").AsObject());
            var bobPublicKey = Assert.IsType<FenObject>(bobKeyPair.Get("publicKey").AsObject());

            var derivedAesAlgorithm = CreateAlgorithm("AES-GCM");
            derivedAesAlgorithm.Set("length", FenValue.FromNumber(128));

            var aliceDeriveAlgorithm = CreateAlgorithm("ECDH");
            aliceDeriveAlgorithm.Set("public", FenValue.FromObject(bobPublicKey));
            var aliceDerivedKeyResult = deriveKey.Invoke(
                new[]
                {
                    FenValue.FromObject(aliceDeriveAlgorithm),
                    FenValue.FromObject(alicePrivateKey),
                    FenValue.FromObject(derivedAesAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("encrypt", "decrypt"))
                },
                null);
            var aliceDerivedKeyThenable = AssertThenableState(aliceDerivedKeyResult, "fulfilled");
            var aliceDerivedKey = Assert.IsType<FenObject>(aliceDerivedKeyThenable.Get("__result").AsObject());

            var bobDeriveAlgorithm = CreateAlgorithm("ECDH");
            bobDeriveAlgorithm.Set("public", FenValue.FromObject(alicePublicKey));
            var bobDerivedKeyResult = deriveKey.Invoke(
                new[]
                {
                    FenValue.FromObject(bobDeriveAlgorithm),
                    FenValue.FromObject(bobPrivateKey),
                    FenValue.FromObject(CreateAesGcmAlgorithm(128)),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("encrypt", "decrypt"))
                },
                null);
            var bobDerivedKeyThenable = AssertThenableState(bobDerivedKeyResult, "fulfilled");
            var bobDerivedKey = Assert.IsType<FenObject>(bobDerivedKeyThenable.Get("__result").AsObject());

            var operationAlgorithm = CreateAlgorithm("AES-GCM");
            operationAlgorithm.Set("iv", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x61, 0x62, 0x63, 0x64 })));
            var plaintext = FenValue.FromString("ecdh-derived-key-message");

            var encryptResult = encrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(aliceDerivedKey),
                    plaintext
                },
                null);
            var encryptThenable = AssertThenableState(encryptResult, "fulfilled");
            var encryptedPayload = Assert.IsType<JsArrayBuffer>(encryptThenable.Get("__result").AsObject());

            var decryptResult = decrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(bobDerivedKey),
                    FenValue.FromObject(encryptedPayload)
                },
                null);
            var decryptThenable = AssertThenableState(decryptResult, "fulfilled");
            var decryptedPayload = Assert.IsType<JsArrayBuffer>(decryptThenable.Get("__result").AsObject());
            Assert.Equal("ecdh-derived-key-message", Encoding.UTF8.GetString(decryptedPayload.Data));
        }

        [Fact]
        public void JsCrypto_SubtleImportEcdhPublicKey_WithUsages_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var generateKey = subtle.Get("generateKey").AsFunction();
            var exportKey = subtle.Get("exportKey").AsFunction();
            var importKey = subtle.Get("importKey").AsFunction();

            var ecdhAlgorithm = CreateAlgorithm("ECDH");
            ecdhAlgorithm.Set("namedCurve", FenValue.FromString("P-256"));
            var keyPairResult = generateKey.Invoke(
                new[]
                {
                    FenValue.FromObject(ecdhAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("deriveBits"))
                },
                null);

            var keyPairThenable = AssertThenableState(keyPairResult, "fulfilled");
            var keyPair = Assert.IsType<FenObject>(keyPairThenable.Get("__result").AsObject());
            var publicKey = Assert.IsType<FenObject>(keyPair.Get("publicKey").AsObject());

            var publicExportResult = exportKey.Invoke(
                new[]
                {
                    FenValue.FromString("spki"),
                    FenValue.FromObject(publicKey)
                },
                null);

            var publicExportThenable = AssertThenableState(publicExportResult, "fulfilled");
            var publicSpki = Assert.IsType<JsArrayBuffer>(publicExportThenable.Get("__result").AsObject());

            var importAlgorithm = CreateAlgorithm("ECDH");
            importAlgorithm.Set("namedCurve", FenValue.FromString("P-256"));
            var importResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("spki"),
                    FenValue.FromObject(publicSpki),
                    FenValue.FromObject(importAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("deriveBits"))
                },
                null);

            var thenable = AssertThenableState(importResult, "rejected");
            Assert.Contains("InvalidAccessError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleImportEcdhKey_CurveMismatch_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();

            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
            var privateBytes = ecdh.ExportPkcs8PrivateKey();

            var importAlgorithm = CreateAlgorithm("ECDH");
            importAlgorithm.Set("namedCurve", FenValue.FromString("P-256"));
            var importResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("pkcs8"),
                    FenValue.FromObject(CreateArrayBuffer(privateBytes)),
                    FenValue.FromObject(importAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("deriveBits"))
                },
                null);

            var thenable = AssertThenableState(importResult, "rejected");
            Assert.Contains("DataError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleDeriveBits_Pbkdf2_ResolvesArrayBuffer()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var deriveBits = subtle.Get("deriveBits").AsFunction();

            var baseKeyResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("fen-password-material"))),
                    FenValue.FromObject(CreateAlgorithm("PBKDF2")),
                    FenValue.FromBoolean(false),
                    FenValue.FromObject(CreateStringArray("deriveBits"))
                },
                null);

            var baseKeyThenable = AssertThenableState(baseKeyResult, "fulfilled");
            var baseKey = Assert.IsType<FenObject>(baseKeyThenable.Get("__result").AsObject());

            var deriveAlgorithm = CreateAlgorithm("PBKDF2", "SHA-256");
            deriveAlgorithm.Set("salt", FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("fen-salt-value"))));
            deriveAlgorithm.Set("iterations", FenValue.FromNumber(1000));

            var deriveResult = deriveBits.Invoke(
                new[]
                {
                    FenValue.FromObject(deriveAlgorithm),
                    FenValue.FromObject(baseKey),
                    FenValue.FromNumber(128)
                },
                null);

            var deriveThenable = AssertThenableState(deriveResult, "fulfilled");
            var buffer = Assert.IsType<JsArrayBuffer>(deriveThenable.Get("__result").AsObject());
            Assert.Equal(16, buffer.Data.Length);
        }

        [Fact]
        public void JsCrypto_SubtleDeriveKey_Pbkdf2_ToAesGcm_EncryptDecrypt_RoundTrips()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var deriveKey = subtle.Get("deriveKey").AsFunction();
            var encrypt = subtle.Get("encrypt").AsFunction();
            var decrypt = subtle.Get("decrypt").AsFunction();

            var baseKeyResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("fen-derive-key-source"))),
                    FenValue.FromObject(CreateAlgorithm("PBKDF2")),
                    FenValue.FromBoolean(false),
                    FenValue.FromObject(CreateStringArray("deriveKey"))
                },
                null);

            var baseKeyThenable = AssertThenableState(baseKeyResult, "fulfilled");
            var baseKey = Assert.IsType<FenObject>(baseKeyThenable.Get("__result").AsObject());

            var deriveAlgorithm = CreateAlgorithm("PBKDF2", "SHA-256");
            deriveAlgorithm.Set("salt", FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("fen-derive-salt"))));
            deriveAlgorithm.Set("iterations", FenValue.FromNumber(1200));

            var derivedAesAlgorithm = CreateAlgorithm("AES-GCM");
            derivedAesAlgorithm.Set("length", FenValue.FromNumber(128));

            var deriveKeyResult = deriveKey.Invoke(
                new[]
                {
                    FenValue.FromObject(deriveAlgorithm),
                    FenValue.FromObject(baseKey),
                    FenValue.FromObject(derivedAesAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("encrypt", "decrypt"))
                },
                null);

            var derivedKeyThenable = AssertThenableState(deriveKeyResult, "fulfilled");
            var derivedKey = Assert.IsType<FenObject>(derivedKeyThenable.Get("__result").AsObject());

            var operationAlgorithm = CreateAlgorithm("AES-GCM");
            operationAlgorithm.Set("iv", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x51, 0x52, 0x53 })));
            var plaintext = FenValue.FromString("pbkdf2-derived-aes-message");

            var encryptResult = encrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(derivedKey),
                    plaintext
                },
                null);

            var encryptThenable = AssertThenableState(encryptResult, "fulfilled");
            var encryptedPayload = Assert.IsType<JsArrayBuffer>(encryptThenable.Get("__result").AsObject());

            var decryptResult = decrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(derivedKey),
                    FenValue.FromObject(encryptedPayload)
                },
                null);

            var decryptThenable = AssertThenableState(decryptResult, "fulfilled");
            var decryptedPayload = Assert.IsType<JsArrayBuffer>(decryptThenable.Get("__result").AsObject());
            Assert.Equal("pbkdf2-derived-aes-message", Encoding.UTF8.GetString(decryptedPayload.Data));
        }

        [Fact]
        public void JsCrypto_SubtleDeriveKey_WithoutDeriveKeyUsage_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var deriveKey = subtle.Get("deriveKey").AsFunction();

            var baseKeyResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("derive-usage-check-key"))),
                    FenValue.FromObject(CreateAlgorithm("PBKDF2")),
                    FenValue.FromBoolean(false),
                    FenValue.FromObject(CreateStringArray("deriveBits"))
                },
                null);

            var baseKeyThenable = AssertThenableState(baseKeyResult, "fulfilled");
            var baseKey = Assert.IsType<FenObject>(baseKeyThenable.Get("__result").AsObject());

            var deriveAlgorithm = CreateAlgorithm("PBKDF2", "SHA-256");
            deriveAlgorithm.Set("salt", FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("derive-usage-salt"))));
            deriveAlgorithm.Set("iterations", FenValue.FromNumber(800));

            var derivedAesAlgorithm = CreateAlgorithm("AES-GCM");
            derivedAesAlgorithm.Set("length", FenValue.FromNumber(128));

            var deriveKeyResult = deriveKey.Invoke(
                new[]
                {
                    FenValue.FromObject(deriveAlgorithm),
                    FenValue.FromObject(baseKey),
                    FenValue.FromObject(derivedAesAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("encrypt", "decrypt"))
                },
                null);

            var thenable = AssertThenableState(deriveKeyResult, "rejected");
            Assert.Contains("InvalidAccessError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void JsCrypto_SubtleDeriveBits_Hkdf_ResolvesArrayBuffer()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var deriveBits = subtle.Get("deriveBits").AsFunction();

            var baseKeyResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("fen-hkdf-base-key-material"))),
                    FenValue.FromObject(CreateAlgorithm("HKDF")),
                    FenValue.FromBoolean(false),
                    FenValue.FromObject(CreateStringArray("deriveBits"))
                },
                null);

            var baseKeyThenable = AssertThenableState(baseKeyResult, "fulfilled");
            var baseKey = Assert.IsType<FenObject>(baseKeyThenable.Get("__result").AsObject());

            var deriveAlgorithm = CreateAlgorithm("HKDF", "SHA-256");
            deriveAlgorithm.Set("salt", FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("fen-hkdf-salt"))));
            deriveAlgorithm.Set("info", FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("fen-hkdf-info"))));

            var deriveResult = deriveBits.Invoke(
                new[]
                {
                    FenValue.FromObject(deriveAlgorithm),
                    FenValue.FromObject(baseKey),
                    FenValue.FromNumber(256)
                },
                null);

            var deriveThenable = AssertThenableState(deriveResult, "fulfilled");
            var buffer = Assert.IsType<JsArrayBuffer>(deriveThenable.Get("__result").AsObject());
            Assert.Equal(32, buffer.Data.Length);
        }

        [Fact]
        public void JsCrypto_SubtleDeriveKey_Hkdf_ToAesGcm_EncryptDecrypt_RoundTrips()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var deriveKey = subtle.Get("deriveKey").AsFunction();
            var encrypt = subtle.Get("encrypt").AsFunction();
            var decrypt = subtle.Get("decrypt").AsFunction();

            var baseKeyResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("fen-hkdf-derive-key-source"))),
                    FenValue.FromObject(CreateAlgorithm("HKDF")),
                    FenValue.FromBoolean(false),
                    FenValue.FromObject(CreateStringArray("deriveKey"))
                },
                null);

            var baseKeyThenable = AssertThenableState(baseKeyResult, "fulfilled");
            var baseKey = Assert.IsType<FenObject>(baseKeyThenable.Get("__result").AsObject());

            var deriveAlgorithm = CreateAlgorithm("HKDF", "SHA-256");
            deriveAlgorithm.Set("salt", FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("fen-hkdf-derive-salt"))));
            deriveAlgorithm.Set("info", FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("fen-hkdf-derive-info"))));

            var derivedAesAlgorithm = CreateAlgorithm("AES-GCM");
            derivedAesAlgorithm.Set("length", FenValue.FromNumber(128));

            var deriveKeyResult = deriveKey.Invoke(
                new[]
                {
                    FenValue.FromObject(deriveAlgorithm),
                    FenValue.FromObject(baseKey),
                    FenValue.FromObject(derivedAesAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("encrypt", "decrypt"))
                },
                null);

            var derivedKeyThenable = AssertThenableState(deriveKeyResult, "fulfilled");
            var derivedKey = Assert.IsType<FenObject>(derivedKeyThenable.Get("__result").AsObject());

            var operationAlgorithm = CreateAlgorithm("AES-GCM");
            operationAlgorithm.Set("iv", FenValue.FromObject(CreateArrayBuffer(new byte[] { 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x71, 0x72, 0x73 })));
            var plaintext = FenValue.FromString("hkdf-derived-aes-message");

            var encryptResult = encrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(derivedKey),
                    plaintext
                },
                null);

            var encryptThenable = AssertThenableState(encryptResult, "fulfilled");
            var encryptedPayload = Assert.IsType<JsArrayBuffer>(encryptThenable.Get("__result").AsObject());

            var decryptResult = decrypt.Invoke(
                new[]
                {
                    FenValue.FromObject(operationAlgorithm),
                    FenValue.FromObject(derivedKey),
                    FenValue.FromObject(encryptedPayload)
                },
                null);

            var decryptThenable = AssertThenableState(decryptResult, "fulfilled");
            var decryptedPayload = Assert.IsType<JsArrayBuffer>(decryptThenable.Get("__result").AsObject());
            Assert.Equal("hkdf-derived-aes-message", Encoding.UTF8.GetString(decryptedPayload.Data));
        }

        [Fact]
        public void JsCrypto_SubtleDeriveKey_Hkdf_WithoutDeriveKeyUsage_Rejects()
        {
            var subtle = GetSubtle(new JsCrypto());
            var importKey = subtle.Get("importKey").AsFunction();
            var deriveKey = subtle.Get("deriveKey").AsFunction();

            var baseKeyResult = importKey.Invoke(
                new[]
                {
                    FenValue.FromString("raw"),
                    FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("hkdf-derive-usage-check-key"))),
                    FenValue.FromObject(CreateAlgorithm("HKDF")),
                    FenValue.FromBoolean(false),
                    FenValue.FromObject(CreateStringArray("deriveBits"))
                },
                null);

            var baseKeyThenable = AssertThenableState(baseKeyResult, "fulfilled");
            var baseKey = Assert.IsType<FenObject>(baseKeyThenable.Get("__result").AsObject());

            var deriveAlgorithm = CreateAlgorithm("HKDF", "SHA-256");
            deriveAlgorithm.Set("salt", FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("hkdf-derive-usage-salt"))));
            deriveAlgorithm.Set("info", FenValue.FromObject(CreateArrayBuffer(Encoding.UTF8.GetBytes("hkdf-derive-usage-info"))));

            var derivedAesAlgorithm = CreateAlgorithm("AES-GCM");
            derivedAesAlgorithm.Set("length", FenValue.FromNumber(128));

            var deriveKeyResult = deriveKey.Invoke(
                new[]
                {
                    FenValue.FromObject(deriveAlgorithm),
                    FenValue.FromObject(baseKey),
                    FenValue.FromObject(derivedAesAlgorithm),
                    FenValue.FromBoolean(true),
                    FenValue.FromObject(CreateStringArray("encrypt", "decrypt"))
                },
                null);

            var thenable = AssertThenableState(deriveKeyResult, "rejected");
            Assert.Contains("InvalidAccessError", thenable.Get("__reason").ToString(), StringComparison.Ordinal);
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

        private static FenObject CreateAlgorithmWithNamedCurve(string name, string namedCurve)
        {
            var algorithm = CreateAlgorithm(name);
            algorithm.Set("namedCurve", FenValue.FromString(namedCurve));
            return algorithm;
        }

        private static FenObject CreateAesGcmAlgorithm(int lengthBits)
        {
            var algorithm = CreateAlgorithm("AES-GCM");
            algorithm.Set("length", FenValue.FromNumber(lengthBits));
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
