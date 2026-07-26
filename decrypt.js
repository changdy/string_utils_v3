const CryptoJS = require("crypto-js");
const crypto = require('crypto');
const forge = require('node-forge');


const solver = {name: "decrypt", describe: "解密文本"};


const key = CryptoJS.enc.Utf8.parse(process.env["jsutils_key"] ?? "");
const iv = CryptoJS.enc.Utf8.parse(process.env["jsutils_iv"] ?? "");
const bodyPubKey = (process.env["jsutils_public_key"] ?? "").trim();
const bodyPrivKey = (process.env["jsutils_private_key"] ?? "").trim();


solver.check = (logs, arr, jsonFlag) => {
    if (arr.length === 1 && key && !jsonFlag) {
        let password = parserPassword(logs);
        return password !== "" && password.length > 3 ? 150 : 0;
    }
    if (bodyPubKey && jsonFlag && logs.startsWith("{")) {
        const parse = JSON.parse(logs);
        if (parse.aesKey && parse.data) {
            return 300;
        }
    }
    return 0;
};


function parserPassword(encrypted) {
    let decrypted = CryptoJS.AES.decrypt(encrypted, key, {
        iv: iv,
        mode: CryptoJS.mode.ECB,
        padding: CryptoJS.pad.Pkcs7,
    });
    return decrypted.toString(CryptoJS.enc.Utf8) ?? "";
}

solver.transfer = (logs, arr, jsonFlag) => {
    if (arr.length === 1 && key && !jsonFlag) {
        solver.nextStep = "";
        return parserPassword(logs);
    }
    if (jsonFlag) {
        const parse = JSON.parse(logs);
        solver.nextStep = "json-view";
        try {
            return decrypt(parse.data, decryptAesKey(parse.aesKey, bodyPubKey));
        } catch (err) {
            return decrypt(parse.data, privdecrypt(parse.aesKey, bodyPrivKey));
        }
    }
}


// 使用服务端的公钥进行AES密钥
function decryptAesKey(encodeAeskey, pubencryptKey) {
    const publicKeyAll = '-----BEGIN PUBLIC KEY-----\n' + pubencryptKey + '\n-----END PUBLIC KEY-----'
    return crypto.publicDecrypt(publicKeyAll, Buffer.from(encodeAeskey.toString('base64'), 'base64'))
}


function decrypt(data, AES_KEY) {
    const decrypted = CryptoJS.AES.decrypt(data, CryptoJS.enc.Utf8.parse(AES_KEY), {
        mode: CryptoJS.mode.ECB, padding: CryptoJS.pad.Pkcs7
    })
    return decrypted.toString(CryptoJS.enc.Utf8)
}

// 使用自己的私钥解密AES密钥
function privdecrypt(encryptedAesKey, privateDecryptKey) {
    const privateKeyAll = '-----BEGIN PRIVATE KEY-----\n' + privateDecryptKey + '\n-----END PRIVATE KEY-----';
    var privateKey = forge.pki.privateKeyFromPem(privateKeyAll);
    return privateKey.decrypt(forge.util.decode64(encryptedAesKey), 'RSAES-PKCS1-V1_5');
}


module.exports = {
    solver,
};
