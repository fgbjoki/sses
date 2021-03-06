﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Security.Certificates;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;
using X509Certificate = Org.BouncyCastle.X509.X509Certificate;
using X509Extension = Org.BouncyCastle.Asn1.X509.X509Extension;

namespace Common.CertificateManager
{
    public class CertificateManager : ICertificateManager
    {
        private static object _sync = new object();
        private static ICertificateManager _instance = null;
        private const int RSAKeyStrength = 2048;
        private const string SignatureAlgorithm = "SHA512WithRSA";

        public static ICertificateManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_sync)
                    {
                        if (_instance == null)
                        {
                            _instance = new CertificateManager();
                        }
                    }
                }

                return _instance;
            }
        }

        private CertificateManager()
        {

        }

        public X509Certificate2 GetCertificateFromStore(StoreLocation storeLocation, StoreName storeName, string subjectName)
        {
            if (subjectName == null)
            {
                throw new ArgumentNullException(nameof(subjectName));
            }

            X509Certificate2 certificate = null;

            using (X509Store store = new X509Store(storeName, storeLocation))
            {
                store.Open(OpenFlags.ReadOnly);
                certificate = store.Certificates.Find(X509FindType.FindBySubjectName, subjectName, true)[0];
                store.Close();
            }

            return certificate;
        }

        public X509Certificate2 GetPublicCertificateFromFile(string filePath)
        {
            if (filePath == null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            return new X509Certificate2(filePath);
        }

        public X509Certificate2 GetPrivateCertificateFromFile(string filePath, string password)
        {
            if (filePath == null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            if (password == null)
            {
                throw new ArgumentNullException(nameof(password));
            }

            return new X509Certificate2(filePath, password, X509KeyStorageFlags.Exportable);
        }

        public string CreateAndStoreNewClientCertificate(string subjectName, string pvkPass, X509Certificate2 issuer)
        {
            X509V3CertificateGenerator generator = new X509V3CertificateGenerator();

            // Generate pseudo random number
            var randomGen = new CryptoApiRandomGenerator();
            var random = new SecureRandom(randomGen);

            // Set certificate serial number
            var serialNumber =
                BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(Int64.MaxValue), random);
            generator.SetSerialNumber(serialNumber);

            // Set certificate subject name
            var subjectDN = new X509Name(subjectName);
            generator.SetSubjectDN(subjectDN);

            // Set issuer subject name
            var issuerDN = new X509Name(issuer.Subject);
            generator.SetIssuerDN(issuerDN);

            // Set certificate validity
            var notBefore = DateTime.UtcNow.Date;
            generator.SetNotBefore(notBefore);
            generator.SetNotAfter(notBefore.AddYears(2));

            // Generate new RSA key pair for certificate
            var keyGeneratorParameters = new KeyGenerationParameters(random, RSAKeyStrength);
            var keyPairGenerator = new RsaKeyPairGenerator();
            keyPairGenerator.Init(keyGeneratorParameters);
            var subjectKeyPair = keyPairGenerator.GenerateKeyPair();

            // Import public key into generator
            generator.SetPublicKey(subjectKeyPair.Public);

            var issuerKeyPair = DotNetUtilities.GetKeyPair(issuer.PrivateKey);

            // Get key pair from .net issuer certificate
            //var issuerKeyPair = DotNetUtilities.GetKeyPair(issuer.PrivateKey);
            var issuerSerialNumber = new BigInteger(issuer.GetSerialNumber());

            // Sign CA key with serial
            var caKeyIdentifier = new AuthorityKeyIdentifier(
                SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(issuerKeyPair.Public),
                new GeneralNames(new GeneralName(issuerDN)),
                issuerSerialNumber);

            generator.AddExtension(
                X509Extensions.AuthorityKeyIdentifier.Id,
                false,
                caKeyIdentifier);

            // Create signature factory to sign new cert
            ISignatureFactory signatureFactory = new Asn1SignatureFactory(SignatureAlgorithm, issuerKeyPair.Private);

            // Generate new bouncy castle certificate signed by issuer
            var newCertificate = generator.Generate(signatureFactory);

            var store = new Pkcs12Store();
            string friendlyName = newCertificate.SubjectDN.ToString().Split('=')[1];

            var certificateEntry = new X509CertificateEntry(newCertificate);
            // Set certificate
            store.SetCertificateEntry(friendlyName, certificateEntry);
            // Set private key
            store.SetKeyEntry(
                friendlyName,
                new AsymmetricKeyEntry(subjectKeyPair.Private),
                new X509CertificateEntry[] { certificateEntry });

            var privatePath = @".\certs\" + $"{friendlyName}.pfx";
            var publicPath = @".\certs\" + $"{friendlyName}.cer";

            using (var stream = new MemoryStream())
            {
                // Convert bouncy castle cert => .net cert
                store.Save(stream, pvkPass.ToCharArray(), random);
                var dotNetCertificate = new X509Certificate2(
                    stream.ToArray(),
                    pvkPass,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

                // Extract public part to store in server storage
                var publicCert = dotNetCertificate.Export(X509ContentType.Cert);
                // Extract private parameters to export into .pfx for distribution
                var privateCert = dotNetCertificate.Export(X509ContentType.Pfx, pvkPass);

                dotNetCertificate.Reset();
                dotNetCertificate.Import(publicCert);

                // Store public cert info in storage
                using (var storage = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                {
                    storage.Open(OpenFlags.ReadWrite);
                    storage.Add(dotNetCertificate);
                    storage.Close();
                }

                dotNetCertificate.Dispose();

                // Write private parameters to .pfx file to install at client
                File.WriteAllBytes(privatePath, privateCert);
                File.WriteAllBytes(publicPath, publicCert);
            }

            return privatePath;
        }
    }
}
