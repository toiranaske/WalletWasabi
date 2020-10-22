using System.Collections.Generic;
using System.Linq;
using System.Text;
using NBitcoin;
using NBitcoin.Secp256k1;
using WalletWasabi.Crypto;
using WalletWasabi.Crypto.Groups;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Crypto.ZeroKnowledge;
using WalletWasabi.Crypto.ZeroKnowledge.LinearRelation;
using WalletWasabi.Crypto.ZeroKnowledge.NonInteractive;
using WalletWasabi.Helpers;

namespace WalletWasabi.Wabisabi
{
	public class WabiSabiClient
	{
		public WabiSabiClient(
			CoordinatorParameters coordinatorParameters, 
			int numberOfCredentials, 
			WasabiRandom randomNumberGenerator)
		{
			RandomNumberGenerator = Guard.NotNull(nameof(randomNumberGenerator), randomNumberGenerator);
			NumberOfCredentials = Guard.InRangeAndNotNull(nameof(numberOfCredentials), numberOfCredentials, 1, 100);
			CoordinatorParameters = Guard.NotNull(nameof(coordinatorParameters), coordinatorParameters);
			Credentials = new CredentialPool();
		}

		private int NumberOfCredentials { get; }

		private CoordinatorParameters CoordinatorParameters { get; }

		public CredentialPool Credentials { get; }
		 
		private WasabiRandom RandomNumberGenerator { get; }

		public (RegistrationRequest, RegistrationValidationData) CreateRequestForZeroAmount()
		{
			var credentialsToRequest = new IssuanceRequest[NumberOfCredentials];
			var knowledge = new Knowledge[NumberOfCredentials];
			var validationData = new IssuanceValidationData[NumberOfCredentials];

			for (var i = 0; i < NumberOfCredentials; i++)
			{
				var randomness = RandomNumberGenerator.GetScalar(allowZero: false);
				var Ma = randomness * Generators.Gh;

				knowledge[i] = ProofSystem.ZeroProof(Ma, randomness);
				credentialsToRequest[i] = new IssuanceRequest(Ma, Enumerable.Empty<GroupElement>());
				validationData[i] = new IssuanceValidationData(Money.Zero, randomness, Ma);
			}

			var transcript = BuildTransnscript(isNullRequest: true);

			return (
				new RegistrationRequest(
					Money.Zero,
					Enumerable.Empty<CredentialPresentation>(),
					credentialsToRequest,
					Prover.Prove(transcript, knowledge, RandomNumberGenerator)),
				new RegistrationValidationData(
					transcript,
					Enumerable.Empty<Credential>(),
					validationData));
		}

		public (RegistrationRequest, RegistrationValidationData) CreateRequest(
			IEnumerable<Money> amountsToRequest,
			IEnumerable<Credential> credentialsToPresent)
		{
			// Make sure we request always the same number of credentials
			var credentialAmountsToRequest = amountsToRequest.ToList();
			var missingCredentialRequests = NumberOfCredentials - amountsToRequest.Count();
			for (var i = 0; i < missingCredentialRequests; i++)
			{
				credentialAmountsToRequest.Add(Money.Zero);
			}

			// Make sure we present always the same number of credentials (except for Null requests)
			var missingCredentialPresent = NumberOfCredentials - credentialsToPresent.Count();

			var alreadyPresentedZeroCredentials = credentialsToPresent.Where(x => x.Amount.IsZero);
			var availableZeroCredentials = Credentials.ZeroValue.Except(alreadyPresentedZeroCredentials);

			// This should not be possible 
			var availableZeroCredentialCount = availableZeroCredentials.Count();
			if (availableZeroCredentialCount < missingCredentialPresent)
			{
				throw new WabiSabiException(
					WabiSabiErrorCode.NotEnoughZeroCredentialToFillTheRequest,
					$"{missingCredentialPresent} credentials are missing but there are only {availableZeroCredentialCount} zero-value credentials available.");
			}

			credentialsToPresent = credentialsToPresent.Concat(availableZeroCredentials.Take(missingCredentialPresent)).ToList();
			var macsToPresent = credentialsToPresent.Select(x => x.Mac);
			if (macsToPresent.Distinct().Count() < macsToPresent.Count())
			{
				throw new WabiSabiException(WabiSabiErrorCode.CredentialToPresentDuplicated);
			}

			var zs = new List<Scalar>();
			var knowledgeToProve = new List<Knowledge>();
			var presentations = new List<CredentialPresentation>();
			foreach (var credential in credentialsToPresent)
			{
				var z = RandomNumberGenerator.GetScalar();
				var presentation = credential.Present(z);
				presentations.Add(presentation);
				knowledgeToProve.Add(ProofSystem.ShowCredential(presentation, z, credential, CoordinatorParameters));
				zs.Add(z);
			}

			// Generate RangeProofs (or ZeroProof) for each requested credential
			var credentialsToRequest = new IssuanceRequest[NumberOfCredentials];
			var validationData = new IssuanceValidationData[NumberOfCredentials];
			for (var i = 0; i < NumberOfCredentials; i++)
			{
				var amount = credentialAmountsToRequest[i];
				var scalarAmount = new Scalar((ulong)amount.Satoshi);

				var randomness = RandomNumberGenerator.GetScalar(allowZero: false);
				var Ma = scalarAmount * Generators.Gg + randomness * Generators.Gh;

				var (rangeKnowledge, bitCommitments) = ProofSystem.RangeProof(scalarAmount, randomness, Constants.RangeProofWidth, RandomNumberGenerator);
				knowledgeToProve.Add(rangeKnowledge);

				var credentialRequest = new IssuanceRequest(Ma, bitCommitments);
				credentialsToRequest[i] = credentialRequest;
				validationData[i] = new IssuanceValidationData(amount, randomness, Ma);
			}

			// Generate Balance Proof
			var sumOfZ = zs.Sum();
			var cr = credentialsToPresent.Select(x => x.Randomness).Sum();
			var r = validationData.Select(x => x.Randomness).Sum();
			var deltaR = cr + r.Negate();

			var balanceKnowledge = ProofSystem.BalanceProof(sumOfZ, deltaR);
			knowledgeToProve.Add(balanceKnowledge);

			var transcript = BuildTransnscript(isNullRequest: false);
			return (
				new RegistrationRequest(
					amountsToRequest.Sum() - credentialsToPresent.Sum(x => x.Amount.ToMoney()),
					presentations,
					credentialsToRequest,
					Prover.Prove(transcript, knowledgeToProve, RandomNumberGenerator)),
				new RegistrationValidationData(
					transcript,
					credentialsToPresent,
					validationData));
		}

		public void HandleResponse(RegistrationResponse registrationResponse, RegistrationValidationData registrationValidationData)
		{
			Guard.NotNull(nameof(registrationResponse), registrationResponse);
			Guard.NotNull(nameof(registrationValidationData), registrationValidationData);

			var issuedCredentialCount = registrationResponse.IssuedCredentials.Count();
			var requestedCredentialCount = registrationValidationData.Requested.Count();
			if (issuedCredentialCount != NumberOfCredentials)
			{
				throw new WabiSabiException(
					WabiSabiErrorCode.IssuedCredentialNumberMismatch, 
					$"{issuedCredentialCount} issued but {requestedCredentialCount} were requested.");
			}

			var credentials = Enumerable
				.Zip(registrationValidationData.Requested, registrationResponse.IssuedCredentials)
				.Select(x => (Requested: x.First, Issued: x.Second))
				.ToArray();

			var statements = credentials
				.Select(x => ProofSystem.IssuerParameters(CoordinatorParameters, x.Issued, x.Requested.Ma));

			var areCorrectlyIssued = Verifier.Verify(registrationValidationData.Transcript, statements, registrationResponse.Proofs);
			if (!areCorrectlyIssued)
			{
				throw new WabiSabiException(WabiSabiErrorCode.ClientReceivedInvalidProofs);
			}

			var credentialReceived = credentials.Select(x => 
				new Credential(new Scalar((ulong)x.Requested.Amount.Satoshi), x.Requested.Randomness, x.Issued));

			Credentials.UpdateCredentials(credentialReceived, registrationValidationData.Presented);
		}

		private Transcript BuildTransnscript(bool isNullRequest)
		{
			var label = $"UnifiedRegistration/{NumberOfCredentials}/{isNullRequest}";
			var encodedLabel = Encoding.UTF8.GetBytes(label);
			return new Transcript(encodedLabel);
		}
	}
}